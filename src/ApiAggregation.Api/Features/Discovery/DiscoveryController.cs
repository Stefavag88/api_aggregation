using ApiAggregation.Api.ErrorHandling;
using ApiAggregation.Application.Features.Discovery;
using Microsoft.AspNetCore.Mvc;

namespace ApiAggregation.Api.Features.Discovery;

[ApiController]
[Route("api/discovery")]
public sealed class DiscoveryController(DiscoveryService service, IEnumerable<IDiscoveryProvider> providers) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<DiscoveryResponse>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<ActionResult<DiscoveryResponse>> Get([FromQuery] DiscoveryQueryInput input, CancellationToken cancellationToken)
    {
        var query = DiscoveryQueryParser
            .Parse(input, [.. providers.Select(p => p.Descriptor)]);

        if (!query.IsSuccess)
            return ResultHttpExtensions.ToProblem(query.Error!);

        var result = await service.DiscoverAsync(query.Value!, cancellationToken);
        return result.ToActionResult(DiscoveryResponseMapper.Map);
    }
}
