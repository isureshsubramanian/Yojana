using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yojana.Platform.DatabaseTests;

/// <summary>
/// A software stand-in for a phone or security key: an ES256 key pair that answers WebAuthn create() and get() the way
/// a browser hands the answer to the page, so the server's real Fido2 checks run end to end in tests.
/// </summary>
public sealed class SoftwarePasskey : IDisposable
{
    public const string Origin = "http://localhost:5080";
    private const string RelyingPartyId = "localhost";
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private uint counter;

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);
    public byte[] UserHandle { get; private set; } = [];

    /// <summary>The answer to navigator.credentials.create() for the server's creation options.</summary>
    public JsonObject Create(JsonElement options)
    {
        UserHandle = Base64Url(options.GetProperty("user").GetProperty("id").GetString()!);
        var clientData = ClientData("webauthn.create", options.GetProperty("challenge").GetString()!);
        var parameters = key.ExportParameters(false);
        var cose = new CborWriter(CborConformanceMode.Ctap2Canonical);
        cose.WriteStartMap(5);
        cose.WriteInt32(1); cose.WriteInt32(2);     // kty: EC2
        cose.WriteInt32(3); cose.WriteInt32(-7);    // alg: ES256
        cose.WriteInt32(-1); cose.WriteInt32(1);    // crv: P-256
        cose.WriteInt32(-2); cose.WriteByteString(parameters.Q.X!);
        cose.WriteInt32(-3); cose.WriteByteString(parameters.Q.Y!);
        cose.WriteEndMap();
        var credentialLength = new byte[] { (byte)(CredentialId.Length >> 8), (byte)CredentialId.Length };
        // User present, user verified, attested credential data included.
        var authenticatorData = AuthenticatorData(0x01 | 0x04 | 0x40)
            .Concat(new byte[16]).Concat(credentialLength).Concat(CredentialId).Concat(cose.Encode()).ToArray();
        var attestation = new CborWriter(CborConformanceMode.Ctap2Canonical);
        attestation.WriteStartMap(3);
        attestation.WriteTextString("fmt"); attestation.WriteTextString("none");
        attestation.WriteTextString("attStmt"); attestation.WriteStartMap(0); attestation.WriteEndMap();
        attestation.WriteTextString("authData"); attestation.WriteByteString(authenticatorData);
        attestation.WriteEndMap();
        return new JsonObject
        {
            ["id"] = Base64Url(CredentialId), ["rawId"] = Base64Url(CredentialId), ["type"] = "public-key",
            ["response"] = new JsonObject
            {
                ["attestationObject"] = Base64Url(attestation.Encode()), ["clientDataJSON"] = Base64Url(clientData),
                ["transports"] = new JsonArray("internal"),
            },
            ["clientExtensionResults"] = new JsonObject(),
        };
    }

    /// <summary>The answer to navigator.credentials.get(); <paramref name="userVerified"/> false is a device that
    /// skipped the fingerprint, face or PIN check.</summary>
    public JsonObject Get(JsonElement options, bool userVerified = true)
    {
        var clientData = ClientData("webauthn.get", options.GetProperty("challenge").GetString()!);
        counter++;
        var authenticatorData = AuthenticatorData(userVerified ? 0x01 | 0x04 : 0x01);
        var signature = key.SignData(authenticatorData.Concat(SHA256.HashData(clientData)).ToArray(),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return new JsonObject
        {
            ["id"] = Base64Url(CredentialId), ["rawId"] = Base64Url(CredentialId), ["type"] = "public-key",
            ["response"] = new JsonObject
            {
                ["authenticatorData"] = Base64Url(authenticatorData), ["clientDataJSON"] = Base64Url(clientData),
                ["signature"] = Base64Url(signature), ["userHandle"] = Base64Url(UserHandle),
            },
            ["clientExtensionResults"] = new JsonObject(),
        };
    }

    public void Dispose() => key.Dispose();

    private byte[] AuthenticatorData(int flags) => SHA256.HashData(Encoding.UTF8.GetBytes(RelyingPartyId))
        .Append((byte)flags)
        .Concat(new[] { (byte)(counter >> 24), (byte)(counter >> 16), (byte)(counter >> 8), (byte)counter }).ToArray();

    private static byte[] ClientData(string type, string challenge) => JsonSerializer.SerializeToUtf8Bytes(
        new { type, challenge, origin = Origin, crossOrigin = false });

    public static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - padded.Length % 4) % 4));
    }
}
