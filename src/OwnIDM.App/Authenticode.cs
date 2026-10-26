using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace OwnIDM.App;

/// <summary>Authenticode (code signing) checks through WinVerifyTrust.</summary>
public static class Authenticode
{
    /// <summary>
    /// Subject of the certificate that signed <paramref name="path"/> when Windows trusts its signature,
    /// otherwise null (unsigned, tampered with, or signed by an untrusted certificate).
    /// </summary>
    public static string? TrustedSigner(string path)
    {
        if (!Verify(path))
            return null;
        try
        {
#pragma warning disable SYSLIB0057 // only reads the signer; the signature was verified above
            return X509Certificate.CreateFromSignedFile(path).Subject;
#pragma warning restore SYSLIB0057
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the installer may replace this copy: if OwnIDM.exe is signed, the installer must be
    /// signed by the same certificate; an unsigned build accepts any installer that passed the SHA-256 check.
    /// </summary>
    public static bool SameSignerAsThisApp(string installer, out string? error)
    {
        var mine = Environment.ProcessPath is { } exe ? TrustedSigner(exe) : null;
        if (mine is null)
        {
            error = null;
            return true;
        }
        var theirs = TrustedSigner(installer);
        error = theirs is null ? "El instalador descargado no tiene una firma digital válida; no se ha instalado."
              : theirs != mine ? $"El instalador está firmado por «{theirs}», no por el autor de OwnIDM; no se ha instalado."
              : null;
        return error is null;
    }

    private static bool Verify(string path)
    {
        var filePath = Marshal.StringToHGlobalUni(path);
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = filePath };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pFile,
                dwStateAction = WTD_STATEACTION_VERIFY,
            };
            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            int result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(pFile);
            Marshal.FreeHGlobal(filePath);
        }
    }

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);
}
