using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using DocuMind.Core.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DocuMind.Tests;

public sealed class DocumentApiTests : IAsyncLifetime
{
    private readonly TempDatabase _db = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Documents", _db.ConnectionString));
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    internal static MultipartFormDataContent File(string name, string content)
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent { { file, "file", name } };
    }

    [Fact]
    public async Task Upload_List_And_Delete_Should_Work_End_To_End()
    {
        var upload = await _client.PostAsync("/api/documents", File("notes.txt", "Quarterly revenue grew by 12 percent."), Ct);
        upload.StatusCode.ShouldBe(HttpStatusCode.Created);
        var document = (await upload.Content.ReadFromJsonAsync<DocumentInfo>(Ct))!;

        var list = await _client.GetFromJsonAsync<List<DocumentInfo>>("/api/documents", Ct);
        list!.ShouldHaveSingleItem().Name.ShouldBe("notes.txt");

        (await _client.DeleteAsync($"/api/documents/{document.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _client.GetAsync($"/api/documents/{document.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Duplicate_Upload_Should_Return_Conflict()
    {
        await _client.PostAsync("/api/documents", File("a.md", "# Same"), Ct);

        var second = await _client.PostAsync("/api/documents", File("b.md", "# Same"), Ct);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Unsupported_File_Should_Return_Problem_Details()
    {
        var response = await _client.PostAsync("/api/documents", File("photo.jpg", "binary"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Document.UnsupportedType");
    }
}
