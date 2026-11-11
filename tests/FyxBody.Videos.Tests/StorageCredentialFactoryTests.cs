using Azure.Identity;
using FyxBody.Videos.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace FyxBody.Videos.Tests;

public sealed class StorageCredentialFactoryTests
{
    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "CredentialTests";
        public string ContentRootPath { get; set; } = "/tmp";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Theory]
    [InlineData("Development", "local-subscription", true)]
    [InlineData("Development", "", false)]
    [InlineData("Development", null, false)]
    [InlineData("Production", "local-subscription", false)]
    [InlineData("Staging", "local-subscription", false)]
    public void CliSubscriptionIsAnExplicitDevelopmentOnlyOptIn(string environment, string? subscription, bool cli)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:AzureCliSubscription"] = subscription }).Build();
        var credential = StorageCredentialFactory.Create(config, new TestEnvironment(environment));
        if (cli) Assert.IsType<AzureCliCredential>(credential);
        else Assert.IsType<DefaultAzureCredential>(credential);
    }
}
