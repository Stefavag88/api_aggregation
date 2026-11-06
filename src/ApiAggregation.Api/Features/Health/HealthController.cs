using Microsoft.AspNetCore.Mvc;

namespace ApiAggregation.Api.Features.Health;

[ApiController]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet("live")]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status200OK, "application/json")]
    public ActionResult<HealthResponse> Live() => Ok(new HealthResponse("healthy"));
}

public sealed record HealthResponse(string Status);
