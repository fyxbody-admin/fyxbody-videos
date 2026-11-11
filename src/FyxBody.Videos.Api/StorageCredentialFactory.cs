using Azure.Core;
using Azure.Identity;

namespace FyxBody.Videos.Api;

public static class StorageCredentialFactory
{
    public static TokenCredential Create(IConfiguration configuration, IHostEnvironment? environment)
    {
        var subscription = configuration["Storage:AzureCliSubscription"];
        if (environment?.IsDevelopment() == true && !string.IsNullOrWhiteSpace(subscription))
            // CLI accepts subscription or tenant, not both. The subscription selects its tenant.
            return new AzureCliCredential(new AzureCliCredentialOptions { Subscription = subscription.Trim() });
        return new DefaultAzureCredential();
    }
}
