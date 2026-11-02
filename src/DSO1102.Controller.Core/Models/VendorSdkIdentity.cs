using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace DSO1102.Controller.Core.Models;

public sealed record VendorSdkIdentity(string Sha256, string Machine, bool IsKnownBinary)
{
    public const string VerifiedSha256 = "93CA581F4DE649C54957CBD84B25A067ABAA89A078F765B4C7B6958F895C9CC1";

    // Static inspection only: never execute DLL code just to identify a file.
    public static VendorSdkIdentity Inspect(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        using var pe = new PEReader(stream);
        var machine = pe.PEHeaders.CoffHeader.Machine;
        return new(hash, machine.ToString(), machine == System.Reflection.PortableExecutable.Machine.I386 &&
            hash.Equals(VerifiedSha256, StringComparison.Ordinal));
    }

    public static void RequireKnownBinary(string path)
    {
        var identity = Inspect(path);
        if (!identity.IsKnownBinary)
            throw new NotSupportedException($"Unverified vendor DLL: {identity.Machine}, SHA256={identity.Sha256}. " +
                "The reconstructed ABI is approved only for the documented binary. Run sdk-info and compare before enabling hardware calls.");
    }
}
