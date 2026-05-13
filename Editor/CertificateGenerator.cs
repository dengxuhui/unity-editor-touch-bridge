using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.X509;
using System.Net;
using UnityEngine;
using X509Certificate = Org.BouncyCastle.X509.X509Certificate;

namespace MobileBridge.Editor
{
    /// <summary>
    /// Editor-only certificate generator using BouncyCastle.
    ///
    /// Generates a self-signed RSA-2048 / SHA-256 certificate with SAN entries
    /// for localhost, 127.0.0.1, and the machine's detected LAN IP.
    ///
    /// Output: two files at the paths reported by CertificateHelper:
    ///   cert.pem      — X.509 DER encoded, base64 wrapped (CERTIFICATE)
    ///   key-params.xml — RSA private key parameters in XML format
    ///
    /// Why XML for the private key?
    ///   Unity 2022.3 runs on Mono whose System.Security.Cryptography stubs
    ///   throw PlatformNotSupportedException for ImportPkcs8PrivateKey,
    ///   ImportRSAPrivateKey, etc.  RSACryptoServiceProvider.FromXmlString() is
    ///   one of the few key-import APIs that is *actually* implemented on Mono.
    ///   BouncyCastle's DotNetUtilities.ToRSA() produces a real RSACryptoServiceProvider
    ///   (populates RSAParameters directly), so ToXmlString(true) works too.
    ///
    /// This class must NEVER be referenced from MobileBridge.Runtime — it lives
    /// in MobileBridge.Editor (Editor-only asmdef) and depends on BouncyCastle
    /// which is placed under Editor/Plugins/ (excluded from player builds).
    /// </summary>
    public static class CertificateGenerator
    {
        private const int KeyStrength = 2048;

        /// <summary>
        /// Generate a new self-signed TLS certificate and write:
        ///   <see cref="CertificateHelper.CertPath"/>   (cert.pem)
        ///   <see cref="CertificateHelper.KeyXmlPath"/> (key-params.xml)
        /// Returns the loaded X509Certificate2 (with private key) on success.
        /// </summary>
        public static X509Certificate2 Generate()
        {
            string certPath   = CertificateHelper.CertPath;
            string keyXmlPath = CertificateHelper.KeyXmlPath;
            Directory.CreateDirectory(Path.GetDirectoryName(certPath));

            // ── 1. Generate RSA key pair ──────────────────────────────────────
            var random    = new SecureRandom();
            var keyParams = new KeyGenerationParameters(random, KeyStrength);
            var keyGen    = new RsaKeyPairGenerator();
            keyGen.Init(keyParams);
            AsymmetricCipherKeyPair keyPair = keyGen.GenerateKeyPair();

            // ── 2. Build certificate fields ───────────────────────────────────
            var certGen = new X509V3CertificateGenerator();

            certGen.SetSerialNumber(BigIntegers.CreateRandomInRange(
                BigInteger.One, BigInteger.ValueOf(long.MaxValue), random));

            // Validity: yesterday → +365 days (avoids clock-skew rejections)
            certGen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
            certGen.SetNotAfter(DateTime.UtcNow.AddDays(365));

            var subject = new X509Name("CN=UnityMobileBridge,O=Unity,C=US");
            certGen.SetSubjectDN(subject);
            certGen.SetIssuerDN(subject);
            certGen.SetPublicKey(keyPair.Public);

            // ── 3. Extensions ─────────────────────────────────────────────────
            certGen.AddExtension(X509Extensions.BasicConstraints, true,
                new BasicConstraints(false));
            certGen.AddExtension(X509Extensions.KeyUsage, true,
                new KeyUsage(KeyUsage.DigitalSignature | KeyUsage.KeyEncipherment));
            certGen.AddExtension(X509Extensions.ExtendedKeyUsage, false,
                new ExtendedKeyUsage(KeyPurposeID.IdKPServerAuth));
            certGen.AddExtension(X509Extensions.SubjectAlternativeName, false,
                BuildSanExtension());

            // ── 4. Sign ───────────────────────────────────────────────────────
            ISignatureFactory signatureFactory =
                new Asn1SignatureFactory("SHA256WITHRSA", keyPair.Private, random);
            X509Certificate bcCert = certGen.Generate(signatureFactory);

            // ── 5. Write cert.pem ─────────────────────────────────────────────
            byte[] certDer = bcCert.GetEncoded();
            File.WriteAllText(certPath, ToPem("CERTIFICATE", certDer));

            // ── 6. Write key-params.xml ───────────────────────────────────────
            // DotNetUtilities.ToRSA() builds an RSACryptoServiceProvider directly
            // from RSA parameters — no ImportPkcs8PrivateKey stub involved.
            var rsaPrivateKey = (RsaPrivateCrtKeyParameters)keyPair.Private;
            using (RSA rsa = DotNetUtilities.ToRSA(rsaPrivateKey))
            {
                // ToXmlString(true) = include private key components; works on Mono.
                File.WriteAllText(keyXmlPath, rsa.ToXmlString(true));
            }

            Debug.Log($"[MobileBridge] Certificate generated:\n  cert: {certPath}\n  key:  {keyXmlPath}");

            // ── 7. Return X509Certificate2 with private key ───────────────────
            // Reload from disk to match exactly what Load() will do at runtime.
            return CertificateHelper.Load();
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static GeneralNames BuildSanExtension()
        {
            var names = new System.Collections.Generic.List<GeneralName>
            {
                new GeneralName(GeneralName.DnsName, "localhost"),
                new GeneralName(GeneralName.IPAddress,
                    new DerOctetString(IPAddress.Loopback.GetAddressBytes())),
            };

            string lanIp = CertificateHelper.GetLocalIPAddress();
            if (!string.IsNullOrEmpty(lanIp))
            {
                try
                {
                    names.Add(new GeneralName(GeneralName.IPAddress,
                        new DerOctetString(IPAddress.Parse(lanIp).GetAddressBytes())));
                    Debug.Log($"[MobileBridge] Certificate SAN includes LAN IP: {lanIp}");
                }
                catch
                {
                    Debug.LogWarning($"[MobileBridge] Could not parse LAN IP '{lanIp}'; skipping SAN entry.");
                }
            }
            else
            {
                Debug.LogWarning("[MobileBridge] Could not detect local IP; SAN will not include LAN IP.");
            }

            return new GeneralNames(names.ToArray());
        }

        /// <summary>Wrap raw DER bytes in PEM armor (RFC 7468, 64-char lines).</summary>
        private static string ToPem(string label, byte[] der)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"-----BEGIN {label}-----");
            string b64 = Convert.ToBase64String(der);
            for (int i = 0; i < b64.Length; i += 64)
                sb.AppendLine(b64.Substring(i, Math.Min(64, b64.Length - i)));
            sb.AppendLine($"-----END {label}-----");
            return sb.ToString();
        }
    }
}
