namespace NeonCompanion.Runtime.Donation
{
    public interface IDonationService
    {
        bool IsDonationSupported { get; }
        void OpenDonationPage();
    }
}
