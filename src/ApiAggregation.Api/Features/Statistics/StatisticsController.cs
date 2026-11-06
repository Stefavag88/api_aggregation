using ApiAggregation.Application.Features.Statistics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ApiAggregation.Api.Features.Statistics;

[ApiController]
[Authorize]
[Route("api/statistics")]
public sealed class StatisticsController(IStatisticsReader reader) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<StatisticsResponse>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status401Unauthorized)]
    public ActionResult<StatisticsResponse> Get()
    {
        var snapshot = reader.GetSnapshot();

        return Ok(new StatisticsResponse(snapshot.StartedAt, snapshot.SnapshotAt, snapshot.Providers));
    }
}
