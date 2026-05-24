using UnityEngine;

namespace NeonCompanion.Runtime.Donation
{
    public sealed class DonationService : IDonationService
    {
        private const string GitHubSponsorsUrl = "https://github.com/sponsors/isteelfelix";
        private const string BuyMeACoffeeUrl = "https://buymeacoffee.com/isteelfelix";

        public bool IsDonationSupported =>
            !string.IsNullOrWhiteSpace(GitHubSponsorsUrl) ||
            !string.IsNullOrWhiteSpace(BuyMeACoffeeUrl);

        public void OpenDonationPage()
        {
            if (!string.IsNullOrWhiteSpace(GitHubSponsorsUrl))
            {
                Application.OpenURL(GitHubSponsorsUrl);
                return;
            }

            if (!string.IsNullOrWhiteSpace(BuyMeACoffeeUrl))
                Application.OpenURL(BuyMeACoffeeUrl);
        }
    }
}
