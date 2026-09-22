using Iemas.Domain.Email;

namespace Iemas.Application.Common.Providers;

public interface IEmailProviderAdapterResolver
{
    IEmailProviderAdapter Resolve(EmailProtocol protocol);
}
