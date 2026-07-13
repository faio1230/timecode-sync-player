using System.Reflection;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class E2ECollectionTests
{
    [Fact]
    public void E2ETestClasses_AreAssignedToE2ECollection()
    {
        var e2eTypes = typeof(E2ECollectionTests).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(HasE2ETrait)
            .ToList();

        e2eTypes.Should().NotBeEmpty();
        e2eTypes.Should().OnlyContain(
            t => IsAssignedToE2ECollection(t),
            "GUI E2E は実アプリプロセスとUI Automationを共有するため直列実行する必要がある");
    }

    private static bool HasE2ETrait(MemberInfo type)
        => type.GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(TraitAttribute))
            .Any(a => a.ConstructorArguments.Count == 2
                && (string?)a.ConstructorArguments[0].Value == "Category"
                && (string?)a.ConstructorArguments[1].Value == "E2E");

    private static bool IsAssignedToE2ECollection(MemberInfo type)
        => type.GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(CollectionAttribute))
            .Any(a => a.ConstructorArguments.Count == 1
                && (string?)a.ConstructorArguments[0].Value == "E2E");
}
