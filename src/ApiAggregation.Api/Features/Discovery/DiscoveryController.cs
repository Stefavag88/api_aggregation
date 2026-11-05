using ApiAggregation.Api.ErrorHandling;
using ApiAggregation.Application.Features.Discovery;
using Microsoft.AspNetCore.Mvc;

namespace ApiAggregation.Api.Features.Discovery;

[ApiController]
[Route("api/discovery")]
public sealed class DiscoveryController(DiscoveryService service, IEnumerable<IDiscoveryProvider> providers) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<DiscoveryResponse>> Get([FromQuery] DiscoveryRequest request, CancellationToken cancellationToken)
    {
        var query = DiscoveryQueryParser.Parse(request.ToInput(), providers.Select(p => p.Descriptor).ToArray());
        if (!query.IsSuccess) return ResultHttpExtensions.ToProblem(query.Error!);
        var result = await service.DiscoverAsync(query.Value!, cancellationToken);
        return result.ToActionResult(DiscoveryResponseMapper.Map);
    }
}
