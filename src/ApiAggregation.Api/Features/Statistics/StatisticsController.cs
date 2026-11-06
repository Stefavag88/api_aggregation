using ApiAggregation.Application.Features.Statistics;
using Microsoft.AspNetCore.Mvc;

namespace ApiAggregation.Api.Features.Statistics;

[ApiController]
[Route("api/statistics")]
public sealed class StatisticsController(IStatisticsReader reader) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<StatisticsResponse>(StatusCodes.Status200OK, "application/json")]
    public ActionResult<StatisticsResponse> Get()
    {
        var snapshot = reader.GetSnapshot();

        return Ok(new StatisticsResponse(snapshot.StartedAt, snapshot.SnapshotAt, snapshot.Providers));
    }
}
