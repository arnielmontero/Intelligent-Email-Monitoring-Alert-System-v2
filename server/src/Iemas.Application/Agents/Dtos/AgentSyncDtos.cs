using Iemas.Application.Cases.Dtos;

namespace Iemas.Application.Agents.Dtos;

/// <summary>§75 Agent Synchronization — what the server returns on connect/reconnect. The server remains authoritative (§77); the Agent rebuilds its UI entirely from this.</summary>
public record AgentSyncResponse(
    List<CaseDto> ActionRequired,
    List<CaseDto> Waiting,
    DateTimeOffset ServerTimeUtc);

public record HeartbeatRequest(string? AgentVersion);
