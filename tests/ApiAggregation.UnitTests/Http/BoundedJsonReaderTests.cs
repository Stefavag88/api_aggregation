using ApiAggregation.Infrastructure.Http;
using System.Net;
using System.Text;

namespace ApiAggregation.UnitTests.Http;

public sealed class BoundedJsonReaderTests
{
    [Theory]
    [InlineData(1_048_576, true)]
    [InlineData(1_048_577, false)]
    public async Task EnforcesDecodedByteLimit(int size, bool accepted)
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes('"' + new string('a', size - 2) + '"'));

        var result = await BoundedJsonReader.ReadAsync<string>(content, TestContext.Current.CancellationToken);

        Assert.Equal(accepted, result.IsSuccess);

        if (!accepted)
            Assert.Equal("response_too_large", result.Error!.Code);
    }

    [Fact]
    public async Task EnforcesLimitWhenLengthHeaderLies()
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes('"' + new string('a', 1_048_576) + '"'));
        content.Headers.ContentLength = 2;

        var result = await BoundedJsonReader.ReadAsync<string>(content, TestContext.Current.CancellationToken);

        Assert.Equal("response_too_large", result.Error!.Code);
    }

    [Fact]
    public async Task EnforcesLimitWithoutLengthHeader()
    {
        using var content = new UnknownLengthContent(new string(' ', 1_048_577));

        var result = await BoundedJsonReader.ReadAsync<string>(content, TestContext.Current.CancellationToken);

        Assert.Equal("response_too_large", result.Error!.Code);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("")]
    public async Task MalformedOrNullJsonIsFailure(string json)
    {
        using var content = new StringContent(json);

        var result = await BoundedJsonReader.ReadAsync<string>(content, TestContext.Current.CancellationToken);

        Assert.Equal("invalid_payload", result.Error!.Code);
    }

    private sealed class UnknownLengthContent(string body) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(Encoding.UTF8.GetBytes(body)).AsTask();

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(body)));
    }
}
