using Iemas.Application.Common.Providers;
using Iemas.Domain.Email;

namespace Iemas.Infrastructure.Providers;

public class EmailProviderAdapterResolver : IEmailProviderAdapterResolver
{
    private readonly IEnumerable<IEmailProviderAdapter> _adapters;

    public EmailProviderAdapterResolver(IEnumerable<IEmailProviderAdapter> adapters)
    {
        _adapters = adapters;
    }

    public IEmailProviderAdapter Resolve(EmailProtocol protocol)
    {
        var adapter = _adapters.FirstOrDefault(a => a.Protocol == protocol);
        if (adapter is null)
        {
            throw new NotSupportedException($"No email provider adapter registered for protocol '{protocol}'.");
        }

        return adapter;
    }
}
