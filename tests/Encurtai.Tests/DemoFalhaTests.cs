using Encurtai.Api.Services;
using Xunit;

namespace Encurtai.Tests;

public class DemoFalhaTests
{
    [Fact]
    public void Demo_TesteQuebrado_ParaVerOCiFalhar()
    {
        var svc = new UrlShortenerService(new FakeCodeGenerator("abc123"), new InMemoryUrlStore());

        var r = svc.Shorten("https://exemplo.com");

        Assert.Equal("codigo-errado-de-proposito", r.Code);
    }
}
