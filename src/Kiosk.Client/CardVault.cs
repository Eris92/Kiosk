using System.Security.Cryptography;
using System.Text;

namespace Kiosk.Client;

/// <summary>
/// Leftovers of the former card login (account password sealed with the card's FIDO2 hmac-secret, replaced by Windows'
/// own card + PIN sign-in): a reset or forgotten card still removes its old file from %LOCALAPPDATA%\Kiosk\Cards.
/// </summary>
internal static class CardVault
{
    private static string FileFor(string cardId) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiosk", "Cards",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cardId))) + ".json");

    internal static void Forget(string cardId) { try { File.Delete(FileFor(cardId)); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
