using System.Xml.Linq;

namespace ChargeGlobe.Api.Tests;

/// <summary>
/// The production images restore and build from explicitly copied projects; a project reference that
/// the Dockerfile does not copy only fails at deploy time. This test fails first.
/// </summary>
public class DockerfileTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Theory]
    [InlineData("ChargeGlobe.Api")]
    [InlineData("ChargeGlobe.Jobs")]
    public void The_image_copies_every_project_the_entry_project_references(string entryProject)
    {
        var dockerfile = File.ReadAllText(Path.Combine(RepoRoot, "src", entryProject, "Dockerfile"));

        foreach (var project in ReferenceClosure(entryProject))
        {
            Assert.Contains($"COPY src/{project}/{project}.csproj src/{project}/", dockerfile);
            Assert.Contains($"COPY src/{project}/ src/{project}/", dockerfile);
        }
    }

    /// <summary>ADR-0011: no Docker volumes; every mount is a bind mount relative to the compose file.</summary>
    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData("docker-compose.test.yml")]
    public void Compose_files_mount_only_paths_relative_to_the_compose_file(string composeFile)
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot, composeFile));
        var sources = new List<string>();
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].Trim() != "volumes:")
            {
                continue;
            }

            var indent = lines[index].Length - lines[index].TrimStart().Length;
            Assert.True(indent > 0, $"{composeFile}: top-level volumes are not allowed");
            for (var entry = index + 1; entry < lines.Length && lines[entry].Length - lines[entry].TrimStart().Length > indent; entry++)
            {
                sources.Add(lines[entry].Trim().TrimStart('-').Trim().Split(':')[0]);
            }
        }

        Assert.NotEmpty(sources);
        Assert.All(sources, source => Assert.StartsWith("./", source));
    }

    [Theory]
    [InlineData("src/ChargeGlobe.Api/Dockerfile")]
    [InlineData("src/ChargeGlobe.Jobs/Dockerfile")]
    [InlineData("src/web/Dockerfile")]
    public void Images_declare_no_volumes(string dockerfile) =>
        Assert.DoesNotContain(
            File.ReadAllLines(Path.Combine(RepoRoot, dockerfile)),
            line => line.TrimStart().StartsWith("VOLUME", StringComparison.OrdinalIgnoreCase));

    /// <summary>The entry project and all projects it references, directly or indirectly.</summary>
    private static IReadOnlySet<string> ReferenceClosure(string entryProject)
    {
        var found = new HashSet<string>();
        var pending = new Stack<string>([entryProject]);
        while (pending.TryPop(out var project))
        {
            if (!found.Add(project))
            {
                continue;
            }

            var csproj = XDocument.Load(Path.Combine(RepoRoot, "src", project, $"{project}.csproj"));
            foreach (var reference in csproj.Descendants("ProjectReference"))
            {
                pending.Push(Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value.Replace('\\', '/')));
            }
        }

        return found;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory!.FullName, "ChargeGlobe.sln")))
        {
            directory = directory.Parent;
        }

        return directory.FullName;
    }
}
