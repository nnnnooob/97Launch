using System.Net;
using System.Text;
using FluentAssertions;
using Launcher.Core.Downloads;
using Xunit;

namespace Launcher.Core.Tests;

public sealed class ModrinthResourceServiceTests
{
    [Fact]
    public async Task Searches_with_project_version_loader_and_category_facets()
    {
        Uri? requested = null;
        using var http = new HttpClient(new JsonHandler((request, _) =>
        {
            requested = request.RequestUri;
            return Json("""{"hits":[{"project_id":"abc123","slug":"test-mod","title":"Test Mod","description":"Test","project_type":"mod","icon_url":"https://cdn.example/icon.png","downloads":12000,"categories":["fabric","technology"],"versions":["1.20.1"]}]}""");
        }));
        var service = new ModrinthResourceService(http);

        var projects = await service.SearchAsync("sodium", "mod", "1.20.1", "Fabric", "热度",
            category: "datapack");

        projects.Should().ContainSingle().Which.Should().BeEquivalentTo(new ModrinthProject(
            "abc123", "test-mod", "Test Mod", "Test", "mod", "https://cdn.example/icon.png",
            12000, ["fabric", "technology"], ["1.20.1"]));
        var facets = Uri.UnescapeDataString(requested!.Query.Split("facets=", 2)[1].Split('&')[0]);
        facets.Should().Contain("project_type:mod").And.Contain("versions:1.20.1")
            .And.Contain("categories:fabric").And.Contain("categories:datapack");
    }

    [Fact]
    public async Task Resolves_primary_file_and_required_project_dependencies()
    {
        using var http = new HttpClient(new JsonHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v2/project/root/version")
                return Json("""[{"id":"root-v1","version_type":"release","files":[{"filename":"root.jar","url":"https://cdn.example/root.jar","primary":true,"size":8,"hashes":{"sha1":"111"}}],"dependencies":[{"project_id":"dependency","dependency_type":"required"}]}]""");
            if (path == "/v2/project/dependency/version")
                return Json("""[{"id":"dep-v1","version_type":"release","files":[{"filename":"dependency.jar","url":"https://cdn.example/dependency.jar","primary":true,"size":12,"hashes":{"sha1":"222"}}]}]""");
            throw new InvalidOperationException("Unexpected Modrinth URL: " + request.RequestUri);
        }));
        var service = new ModrinthResourceService(http);
        var root = Path.Combine(Path.GetTempPath(), "97launch-modrinth-tests");
        var project = new ModrinthProject("root", "root", "Root", "", "mod", "", 0, [], ["1.20.1"]);

        var files = await service.ResolveDownloadFilesAsync(project, "1.20.1", "fabric", root);

        files.Should().HaveCount(2);
        files.Select(file => Path.GetFileName(file.Path)).Should().BeEquivalentTo("root.jar", "dependency.jar");
        files.Should().OnlyContain(file => file.Sha1 != null && file.Size > 0 && file.Sources.Count == 1);
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task Lists_real_versions_with_primary_file_and_release_metadata()
    {
        using var http = new HttpClient(new JsonHandler((_, _) => Json("""
            [{"id":"old","name":"Old","version_number":"1.0","version_type":"release","date_published":"2024-01-01T00:00:00Z","game_versions":["1.20.1"],"loaders":["fabric"],"files":[{"filename":"old.jar","url":"https://cdn.example/old.jar","primary":true,"size":12}]},
             {"id":"new","name":"New","version_number":"2.0","version_type":"beta","date_published":"2025-01-01T00:00:00Z","game_versions":["1.21.1"],"loaders":["neoforge"],"files":[{"filename":"new.jar","url":"https://cdn.example/new.jar","primary":true,"size":20}],"dependencies":[{"project_id":"api","dependency_type":"required"}]}]
            """)));
        var versions = await new ModrinthResourceService(http).GetVersionsAsync("project");
        versions.Select(version => version.Id).Should().Equal("new", "old");
        versions[0].PrimaryFile!.Name.Should().Be("new.jar");
        versions[0].Loaders.Should().ContainSingle("neoforge");
        versions[0].Dependencies.Should().ContainSingle(dependency => dependency.ProjectId == "api");
    }

    [Fact]
    public async Task Uses_explicitly_selected_version_and_only_checked_optional_dependencies()
    {
        var paths = new List<string>();
        using var http = new HttpClient(new JsonHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            return path == "/v2/version/selected"
                ? Json("""{"id":"selected","files":[{"filename":"chosen.jar","url":"https://cdn.example/chosen.jar","primary":true,"size":10}],"dependencies":[{"project_id":"optional","dependency_type":"optional"},{"project_id":"unchecked","dependency_type":"optional"}]}""")
                : Json("""[{"id":"optional-version","version_type":"release","files":[{"filename":"optional.jar","url":"https://cdn.example/optional.jar","primary":true,"size":12}]}]""");
        }));
        var project = new ModrinthProject("root", "root", "Root", "", "mod", "", 0, [], ["1.20.1"]);
        var files = await new ModrinthResourceService(http).ResolveDownloadFilesAsync(project, "1.20.1", "fabric",
            Path.GetTempPath(), versionId: "selected", optionalDependencies: new HashSet<string> { "optional" });
        files.Select(file => Path.GetFileName(file.Path)).Should().BeEquivalentTo("chosen.jar", "optional.jar");
        paths.Should().Equal("/v2/version/selected", "/v2/project/optional/version");
    }

    [Fact]
    public async Task Sends_page_offset_and_additional_category_to_search()
    {
        Uri? requested = null;
        using var http = new HttpClient(new JsonHandler((request, _) => { requested = request.RequestUri; return Json("""{"hits":[]}"""); }));
        await new ModrinthResourceService(http).SearchAsync("", "resourcepack", "全部", "任意加载器", "下载量",
            offset: 50, extraCategory: "32x");
        Uri.UnescapeDataString(requested!.Query).Should().Contain("offset=50").And.Contain("categories:32x").And.Contain("index=downloads");
    }

    private sealed class JsonHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request, cancellationToken));
    }
}
