using ApiAggregation.Domain.Discovery;

namespace ApiAggregation.Application.Features.Discovery;

public sealed record ProviderDescriptor(string Name, ContentType Type);
