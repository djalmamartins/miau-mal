using System.Xml.Linq;

namespace Miau.Engine.Tests;

public sealed class ArchitectureTests
{
    [Theory]
    [InlineData("Miau.Core", 0)]
    [InlineData("Miau.Engine", 1)]
    public void Runtime_libraries_keep_dependency_boundaries(string project, int referenceCount)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Miau.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var document = XDocument.Load(Path.Combine(root.FullName, "src", project, $"{project}.csproj"));
        Assert.Empty(document.Descendants("PackageReference"));
        Assert.Empty(document.Descendants("FrameworkReference"));
        var references = document.Descendants("ProjectReference").ToArray();
        Assert.Equal(referenceCount, references.Length);
        if (referenceCount == 1)
        {
            Assert.EndsWith("Miau.Core.csproj", references[0].Attribute("Include")!.Value);
        }
    }
}
