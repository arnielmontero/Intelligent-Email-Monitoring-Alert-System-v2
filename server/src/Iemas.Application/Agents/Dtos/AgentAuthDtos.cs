namespace Iemas.Application.Agents.Dtos;

/// <summary>§68 "Agent Authenticates" step — the Agent exchanges its provisioned Registration Key for a short-lived bearer token used on every subsequent API/SignalR call.</summary>
public record AgentAuthenticateRequest(Guid AgentId, string RegistrationKey);

public record AgentAuthenticateResponse(string AccessToken, DateTimeOffset ExpiresAt, Guid AgentId, Guid EmployeeId, string EmployeeName);

/// <summary>§70 — rotation. The Agent must present its current valid access token to receive a newly rotated Registration Key; the old key becomes invalid immediately (§70 "Invalid after revocation").</summary>
public record RotateAgentCredentialResponse(string RegistrationKey, DateTimeOffset ExpiresAt);
