using System.Text.Json;
using Iemas.Application.Common.Ai;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.EmailClassification;
using Iemas.Application.Operations;
using Iemas.Domain.Ai;
using Iemas.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.AiModels;

/// <summary>One measured run of a model over the benchmark email set.</summary>
public record AiModelBenchmarkResult(
    string ModelIdentifier,
    int Samples,
    int Correct,
    int SentToReview,
    int Failed,
    double AccuracyPercent,
    decimal TotalCostUsd,
    decimal CostPer1000EmailsUsd,
    double AverageLatencyMs,
    int AveragePromptTokens,
    int AverageCompletionTokens,
    DateTimeOffset MeasuredAt,
    IReadOnlyList<string> Mistakes);

/// <summary>A model in the CMS dropdown, with live price and (if measured) benchmark effectiveness.</summary>
public record AiModelRecommendation(
    string ModelIdentifier,
    string Name,
    string Note,
    bool InCatalog,
    decimal? PromptPricePerMillion,
    decimal? CompletionPricePerMillion,
    /// <summary>Measured cost when benchmarked, otherwise estimated from price × typical token use.</summary>
    decimal? CostPer1000EmailsUsd,
    bool CostIsMeasured,
    double? AccuracyPercent,
    double? AverageLatencyMs,
    DateTimeOffset? MeasuredAt,
    string CostTier,
    string EffectivenessTier,
    bool IsBestValue,
    bool IsMostAccurate,
    bool IsCheapest);

/// <summary>
/// "Which model should read our email?" — runs a fixed set of hard SAWO-style emails (the kind the no-AI
/// Zettelkasten filter leaves Uncertain, so exactly what the AI will see in production) through candidate
/// models with the real production prompt, and scores them on the same Important / Not-important decision
/// production makes. Results are stored, so the CMS model dropdown can show measured cost and effectiveness.
/// </summary>
public class AiModelBenchmarkService
{
    public const string ModelBenchmarkPurpose = "ModelBenchmark";
    private const string ResultsKey = "ai.model_benchmark_results";
    private const int MaxModelsPerRun = 8;

    /// <summary>Typical production call when nothing has been measured yet (prompt ≈ rules + email, short JSON answer).</summary>
    private const int EstimatedPromptTokens = 900;
    private const int EstimatedCompletionTokens = 90;

    /// <summary>
    /// Low-cost, real-time models worth considering for email triage (no :batch variants — those are
    /// asynchronous). Only those present in OpenRouter's live catalog are offered.
    /// </summary>
    // Notes reflect the analysis run on 2026-11-10 (28 hard emails, production prompt); re-run from the CMS any time.
    public static readonly IReadOnlyList<(string Id, string Note)> Candidates =
    [
        ("qwen/qwen3-30b-a3b-instruct-2507", "Qwen3 30B: best accuracy at low cost, strong multilingual"),
        ("mistralai/mistral-small-3.2-24b-instruct", "Mistral Small 3.2: near-best accuracy, a good fallback from another vendor"),
        ("anthropic/claude-haiku-5.5", "Claude Haiku 5.5: very accurate, about twice the cost"),
        ("amazon/nova-micro-v1", "Nova Micro: the lowest cost that is still usable; misses some dealer requests"),
        ("openai/gpt-4o-mini", "GPT-4o-mini: the old default; less accurate and pricier than Qwen3"),
        ("mistralai/mistral-nemo", "Mistral Nemo: very cheap but accepts vendor pitches; slow"),
        ("deepseek/deepseek-v4-flash", "DeepSeek V4 Flash: frequent empty/broken replies"),
        ("openai/gpt-4.1-nano", "GPT-4.1-nano: too hesitant; sends most inquiries to review"),
        ("google/gemini-2.5-flash-lite", "Gemini Flash-Lite: often rate-limited on OpenRouter's shared quota"),
        ("meta-llama/llama-3.1-8b-instruct", "Llama 3.1 8B: cheapest, but too weak for this job"),
    ];

    private readonly IAppDbContext _db;
    private readonly IAiClassificationProvider _aiProvider;
    private readonly AiProviderSettingsService _providerSettings;

    public AiModelBenchmarkService(IAppDbContext db, IAiClassificationProvider aiProvider, AiProviderSettingsService providerSettings)
    {
        _db = db;
        _aiProvider = aiProvider;
        _providerSettings = providerSettings;
    }

    public async Task<IReadOnlyList<AiModelBenchmarkResult>> RunAsync(IReadOnlyList<string> modelIdentifiers, CancellationToken cancellationToken)
    {
        var models = modelIdentifiers.Select(m => m.Trim()).Where(m => m.Length > 0).Distinct().Take(MaxModelsPerRun).ToList();
        var rules = await EmailRules.LoadForPromptAsync(_db, cancellationToken);
        var requireResponse = await SystemSettingsService.GetBoolAsync(_db, SystemSettingKeys.RequireResponseForCase, cancellationToken);

        var results = new List<AiModelBenchmarkResult>();
        foreach (var model in models)
        {
            var result = await RunModelAsync(model, rules, requireResponse, cancellationToken);
            results.Add(result);
            // Saved per model, so one problem late in a long run never throws away the models already measured.
            await SaveAsync([result], cancellationToken);
        }

        return results;
    }

    private async Task<AiModelBenchmarkResult> RunModelAsync(
        string model, (string Legitimacy, string Response) rules, bool requireResponse, CancellationToken cancellationToken)
    {
        // Sequential on purpose: the provider pipeline records usage through the request's (non thread-safe) DbContext.
        var outcomeList = new List<(BenchmarkSamples.Sample sample, ClassificationAttemptResult attempt)>();
        foreach (var sample in BenchmarkSamples.All)
        {
            var request = new ClassificationRequest(
                sample.Subject, sample.Body, sample.From, "info@sawo.com", false, "(none)", string.Empty, string.Empty, string.Empty,
                Purpose: ModelBenchmarkPurpose, LegitimacyRules: rules.Legitimacy, ResponseRules: rules.Response);
            outcomeList.Add((sample, await _aiProvider.ClassifyAsync(request, model, TimeSpan.FromSeconds(45), cancellationToken)));
        }
        var outcomes = outcomeList.ToArray();

        int correct = 0, review = 0, failed = 0;
        var mistakes = new List<string>();
        foreach (var (sample, attempt) in outcomes)
        {
            if (!attempt.Succeeded || attempt.Response is null)
            {
                failed++;
                var error = attempt.ErrorMessage ?? "unknown error";
                mistakes.Add($"Failed: \"{sample.Subject}\" ({(error.Length > 120 ? error[..120] + "..." : error)})");
                continue;
            }

            var r = attempt.Response;
            var decision = ClassificationDecisionPolicy.Decide(r.Relevant, r.Confidence, null, null, null, r.Legitimate, r.ResponseExpected, requireResponse).Decision;
            var expected = sample.IsInquiry ? ImportanceDecision.Important : ImportanceDecision.NotImportant;
            if (decision == expected) correct++;
            else
            {
                if (decision == ImportanceDecision.ReviewRequired) review++;
                mistakes.Add($"{(sample.IsInquiry ? "Missed inquiry" : "Accepted junk")}{(decision == ImportanceDecision.ReviewRequired ? " (sent to review)" : "")}: \"{sample.Subject}\"");
            }
        }

        var succeeded = outcomes.Where(o => o.attempt.Succeeded).Select(o => o.attempt).ToList();
        var costs = succeeded.Where(a => a.Usage?.CostUsd is not null).Select(a => a.Usage!.CostUsd!.Value).ToList();
        var totalCost = outcomes.Sum(o => o.attempt.Usage?.CostUsd ?? 0m);
        var perEmail = costs.Count > 0 ? costs.Average() : 0m;

        return new AiModelBenchmarkResult(
            model,
            outcomes.Length,
            correct,
            review,
            failed,
            Math.Round(100.0 * correct / outcomes.Length, 1),
            Math.Round(totalCost, 6),
            Math.Round(perEmail * 1000m, 4),
            succeeded.Count > 0 ? Math.Round(succeeded.Average(a => (double)a.DurationMs)) : 0,
            (int)Math.Round(succeeded.Where(a => a.Usage?.PromptTokens is not null).Select(a => (double)a.Usage!.PromptTokens!.Value).DefaultIfEmpty(0).Average()),
            (int)Math.Round(succeeded.Where(a => a.Usage?.CompletionTokens is not null).Select(a => (double)a.Usage!.CompletionTokens!.Value).DefaultIfEmpty(0).Average()),
            DateTimeOffset.UtcNow,
            mistakes);
    }

    public async Task<IReadOnlyList<AiModelBenchmarkResult>> GetStoredAsync(CancellationToken cancellationToken)
    {
        var row = await _db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == ResultsKey, cancellationToken);
        if (row is null || string.IsNullOrWhiteSpace(row.Value)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<AiModelBenchmarkResult>>(row.Value) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task SaveAsync(IReadOnlyList<AiModelBenchmarkResult> newResults, CancellationToken cancellationToken)
    {
        var merged = (await GetStoredAsync(cancellationToken)).ToDictionary(r => r.ModelIdentifier);
        foreach (var r in newResults) merged[r.ModelIdentifier] = r;

        // Keep the stored JSON small (it lives in a settings row): short mistake lines, at most 30 per model.
        foreach (var key in merged.Keys.ToList())
        {
            var r = merged[key];
            merged[key] = r with { Mistakes = r.Mistakes.Take(30).Select(m => m.Length > 200 ? m[..200] + "..." : m).ToList() };
        }

        // Hidden row: not part of the settings catalog, so it never shows on the System Settings page.
        var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Key == ResultsKey, cancellationToken);
        if (row is null)
        {
            row = new SystemSetting { Key = ResultsKey };
            _db.SystemSettings.Add(row);
        }
        row.Value = JsonSerializer.Serialize(merged.Values.OrderBy(r => r.ModelIdentifier).ToList());
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The dropdown list: candidates (plus any benchmarked model) with live price, measured accuracy and labels.</summary>
    public async Task<IReadOnlyList<AiModelRecommendation>> GetRecommendationsAsync(CancellationToken cancellationToken)
    {
        var catalog = (await _providerSettings.GetCatalogAsync(cancellationToken)).ToDictionary(m => m.Id);
        var measured = (await GetStoredAsync(cancellationToken)).ToDictionary(r => r.ModelIdentifier);

        var ids = Candidates.Select(c => c.Id).Concat(measured.Keys).Distinct().ToList();
        var rows = ids.Select(id =>
        {
            catalog.TryGetValue(id, out var cat);
            measured.TryGetValue(id, out var m);
            var note = Candidates.FirstOrDefault(c => c.Id == id).Note ?? "Benchmarked model";
            decimal? estimated = cat?.PromptPricePerMillion is { } p && cat.CompletionPricePerMillion is { } c
                ? Math.Round((p * EstimatedPromptTokens + c * EstimatedCompletionTokens) / 1000m, 4)
                : null;
            var measuredCost = m is { CostPer1000EmailsUsd: > 0 } ? m.CostPer1000EmailsUsd : (decimal?)null;
            return new
            {
                Id = id, Name = cat?.Name ?? id, Note = note, InCatalog = cat is not null, Cat = cat, M = m,
                Cost = measuredCost ?? estimated, CostIsMeasured = measuredCost is not null,
            };
        }).Where(r => r.InCatalog || r.M is not null).ToList();

        var accurate = rows.Where(r => r.M is not null && r.M.Failed == 0).ToList();
        var bestAccuracy = accurate.Count > 0 ? accurate.Max(r => r.M!.AccuracyPercent) : (double?)null;
        // Best value: the cheapest model whose accuracy is within 3 points of the best measured.
        var bestValue = bestAccuracy is null ? null
            : accurate.Where(r => r.M!.AccuracyPercent >= bestAccuracy - 3 && r.Cost is not null).OrderBy(r => r.Cost).FirstOrDefault()?.Id;
        var mostAccurate = bestAccuracy is null ? null
            : accurate.Where(r => r.M!.AccuracyPercent == bestAccuracy).OrderBy(r => r.Cost ?? decimal.MaxValue).First().Id;
        // "Cheapest" only among models good enough to use (measured Good or better); a cheap model that misses
        // most inquiries is not a saving. Before anything is measured, plain cheapest.
        var usable = accurate.Where(r => r.M!.AccuracyPercent >= 88 && r.Cost is not null).ToList();
        var cheapest = (usable.Count > 0 ? usable : rows.Where(r => r.Cost is not null)).OrderBy(r => r.Cost).FirstOrDefault()?.Id;

        return rows
            .OrderByDescending(r => r.Id == bestValue)
            .ThenByDescending(r => r.M?.AccuracyPercent ?? -1)
            .ThenBy(r => r.Cost ?? decimal.MaxValue)
            .Select(r => new AiModelRecommendation(
                r.Id, r.Name, r.Note, r.InCatalog, r.Cat?.PromptPricePerMillion, r.Cat?.CompletionPricePerMillion,
                r.Cost, r.CostIsMeasured, r.M?.AccuracyPercent, r.M?.AverageLatencyMs, r.M?.MeasuredAt,
                CostTier(r.Cost), EffectivenessTier(r.M),
                r.Id == bestValue, r.Id == mostAccurate, r.Id == cheapest))
            .ToList();
    }

    private static string CostTier(decimal? per1000) => per1000 switch
    {
        null => "Unknown cost",
        < 0.05m => "Very low cost",
        < 0.15m => "Low cost",
        < 0.50m => "Medium cost",
        _ => "High cost",
    };

    private static string EffectivenessTier(AiModelBenchmarkResult? m) => m switch
    {
        null => "Not tested",
        { Failed: > 0 } when m.Failed * 2 >= m.Samples => "Unreliable (calls failed)",
        { AccuracyPercent: >= 95 } => "Excellent",
        { AccuracyPercent: >= 88 } => "Good",
        { AccuracyPercent: >= 75 } => "Fair",
        _ => "Poor",
    };
}

/// <summary>
/// Hard cases modelled on real SAWO website/info@ emails (names and contact details removed). Weighted toward
/// what the Zettelkasten filter leaves Uncertain: short or vague real inquiries, partner requests and polished
/// vendor/service pitches that mention saunas.
/// </summary>
public static class BenchmarkSamples
{
    public record Sample(string Subject, string Body, string From, bool IsInquiry);

    public static readonly IReadOnlyList<Sample> All =
    [
        // ---- Real inquiries (should become a Case) ----
        new("Saunova 2.0 PLUS (SAU-UI-V2-P)", "I'm still waiting about notice for the release of Saunova 2.0 PLUS (SAU-UI-V2-P)", "owner1@gmail.example", true),
        new("Relay clicking in standby mode", "Dear SAWO, this week I noticed a relay clicking sound coming from the sauna power controller. It disappears when I switch on the sauna or the light. Is this normal or does the unit need service?", "home.owner@outlook.example", true),
        new("Electrical Sauna", "Dear Sawo team, we bought a house two years ago including a sauna with a Sawo electric heater. It's wonderful but I'd wish to know about one accessory the previous owner had - a vaporizer bowl. Is it still sold?", "family@web.example", true),
        new("Traditional sauna", "Do you make traditional sauna? Or do you deliver sauna equipment? Tell me more about your services", "client@yahoo.example", true),
        new("Infrared Sauna", "I work as Group Director of Engineering for a global hospitality company with over 500 hotels. We are reviewing infrared sauna options for several properties and would like to understand your range.", "engineering@hotelgroup.example", true),
        new("visit", "Hello, we're building a large house in Panglao and wish to incorporate a sauna. House completion will be in June. We'd like to coordinate a visit to see your products.", "builder@gmail.example", true),
        new("Inquiry", "Hi my ask po if may cellphone number na pwede magamit pag call po for inquiries. Thanks", "buyer.ph@gmail.example", true),
        new("Product support", "Client of mine wants to build a spa using your brand. I need to know if you provide any support in USA and any vendor I can reach out to located near Portland, OR.", "designer@studio.example", true),
        new("richiesta preventivo", "Buonasera, scrivo per conto di una società in Calabria che sta realizzando una struttura wellness. Vorremmo un preventivo per una sauna e un bagno turco.", "ufficio@srl.example", true),
        new("Taloon.com tilattu kiuas", "Tilasin Sawo 6 kW kiukaan erillisellä ohjaimella, tilaus maksettu 18.2. Minulle sanotaan aina että tilausta selvitellään, viimeksi että teillä on vain yksi vaurioitunut paketti. Milloin saan kiukaan?", "asiakas@kolumbus.example", true),
        new("Ajastimet ja termostaatit", "Hei, olemme Etelä-Euroopassa toimiva sauna-alan yritys ja olemme kiinnostuneita SAWO:n ajastimista ja termostaateista. Voisitteko lähettää hinnaston?", "info@saunacompany.example", true),
        new("Coals", "How often should the coals be replaced on our electric wall mounted sauna", "user@gmail.example", true),
        new("Business inquiry", "Hello, we are a large retailer of saunas in Texas and would love to discuss carrying your heaters. Can we set up a time to connect?", "sales@saunaretail.example", true),
        new("Partnership Opportunity", "Hi SAWO Team, I run an online store for premium saunas and cold plunges. I've been researching your sauna heaters and would like to become an authorized reseller. What are your dealer terms?", "founder@recoverystore.example", true),
        new("Hinges for glass doors", "Looking for supplier in North America (Canada) that can supply these?", "contractor@build.example", true),
        new("Repair home sauna", "home sauna repair manila, oasis brand. pwede kayo?", "homeowner@gmail.example", true),

        // ---- Not inquiries (vendors/services selling TO us, marketing, automated) ----
        new("Experience Relaxation with Our Professional Sauna Services", "Dear Valued Customer, are you looking for a relaxing way to unwind? We are pleased to introduce our professional sauna services: traditional and infrared sauna sessions, private sauna room reservations and packages for groups. Contact us today to learn more about our rates. We look forward to serving you!", "promo@spacenter.example", false),
        new("Analyst", "Dear Exhibitor, would you be interested in the International Pool Spa Patio Expo 2026 Attendees List? Reply with 'Send counts and pricing'.", "kayla@datalists.example", false),
        new("Mitsuya Co., Ltd. - Plating Company", "I am writing from a Japan-based specialist in functional plating and surface treatment solutions. We are exploring potential collaboration with sauna heater manufacturers to improve corrosion resistance of heating elements. May we send our company profile?", "sales@plating.example", false),
        new("Product information for sourcing needs", "Hello, I would like to connect with your sourcing department. I represent a timber company - we specialize in producing aspen and thermo aspen benches and wall panels for saunas. Could we send samples and a price list?", "export@timber.example", false),
        new("Get A Wikipedia Page For Your Company", "Wikipedia is the world's most significant reference tool. The Wiki links show up on the 1st page of Google 97% of the time. We can create a page for your sauna brand. Interested?", "wiki@pages.example", false),
        new("A trade show booth in a BAG!", "Hi, I'm with a family-owned fabric printing company. We produce a unique backdrop that fits in a bag - perfect for your next sauna industry trade show. Want a free quote?", "jackie@printing.example", false),
        new("Reliable Cable Supplier - Power/Control Cable", "Dear Sir/Madam, this is Ada from a professional cable manufacturer, ISO/CE/ROHS certified. Our heat-resistant silicone cables are ideal for sauna heaters. Hope to cooperate with you. Can I send our catalogue?", "ada@cable.example", false),
        new("Quick question about your website", "Hi, I came across your website and noticed it isn't ranking for 'sauna heaters'. We help businesses get on the first page of Google. Do you have 15 minutes this week for a quick call?", "growth@seoagency.example", false),
        new("Renew Before December 3 To Take Advantage Of Current Pricing", "Your license expires soon. Renew before December 3 to keep today's price. Are you ready to renew? Manage your subscription in your email preferences.", "list@security-plugin.example", false),
        new("Exploring Collaboration Opportunity", "We are one of the leading exporters of Himalayan Pink Salt and its wellness products, including salt bricks for sauna walls. I need to connect and explore the possibility of collaboration.", "export@saltco.example", false),
        new("Automatic reply: Order 4471", "Thank you for your email. I am out of the office until Monday with limited access to email. For urgent matters please contact my colleague.", "purchasing@partner.example", false),
        new("NICHIYU ELECTRIC FORKLIFT, BATTERIES AND OTHER COMMODITIES", "Dear Ma'am/Sir, I am writing to formally introduce our company, the exclusive supplier of electric forklifts in the Philippines. We would be glad to support your warehouse operations.", "sales@forklift.example", false),
    ];
}
