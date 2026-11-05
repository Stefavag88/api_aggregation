using Microsoft.AspNetCore.Mvc;

namespace ApiAggregation.Api.Features.Health;

[ApiController]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet("live")]
    public IActionResult Live() => Ok(new { status = "healthy" });
}
