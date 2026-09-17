#if WINDOWS10_0_17763_0_OR_GREATER
using System;
using System.Threading.Tasks;
using Windows.Services.Store;

namespace AppAtlas.Sdk.Links
{
    /// <summary>
    /// The windows asset's half of the deferred claim: a packaged app's
    /// campaign id, read from the store so no app code is needed. An
    /// unpackaged app throws out of StoreContext and stays silent — it did
    /// not come through the store, so there is nothing to claim.
    /// </summary>
    internal static class StoreCampaign
    {
        internal static void TryClaim()
        {
            if (AtlasLinks.ClaimSettled) return;

            Task.Run(async () =>
            {
                try
                {
                    var campaignId = await ReadAsync().ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(campaignId)) AtlasLinks.ClaimCampaignId(campaignId);
                }
                catch (Exception)
                {
                    // No identity, no store, or a store hiccup: the manual
                    // ClaimCampaignId path still covers all of them.
                }
            });
        }

        private static async Task<string> ReadAsync()
        {
            var context = StoreContext.GetDefault();

            // A signed-in Microsoft account carries it on the owned SKU.
            var product = await context.GetStoreProductForCurrentAppAsync();

            if (product != null && product.Product != null)
            {
                foreach (var sku in product.Product.Skus)
                {
                    if (!sku.IsInUserCollection) continue;

                    var data = sku.CollectionData;

                    if (data != null && !string.IsNullOrEmpty(data.CampaignId)) return data.CampaignId;
                }
            }

            // Anonymous installs carry it in the license's extended data.
            var license = await context.GetAppLicenseAsync();

            if (license == null || string.IsNullOrEmpty(license.ExtendedJsonData)) return null;

            var parsed = JsonReader.Object(license.ExtendedJsonData);

            return parsed != null && parsed.TryGetValue("customPolicyField1", out var value)
                ? value as string : null;
        }
    }
}
#endif
