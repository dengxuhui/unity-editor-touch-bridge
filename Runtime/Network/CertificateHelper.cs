using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UnityEngine;

namespace MobileBridge
{
    public static class CertificateHelper
    {
        private const string SubDir      = "MobileBridge";
        private const string CertFile    = "bridge.pfx";
        private const string CertPasswd  = "mobileBridge2024";
        private const int    ValidDays   = 365;

        public static string CertPath =>
            Path.Combine(Application.persistentDataPath, SubDir, CertFile);

        public static bool CertExists() => File.Exists(CertPath);

        public static bool NeedsRenewal()
        {
            if (!CertExists()) return true;
            try
            {
                using var cert = new X509Certificate2(CertPath, CertPasswd);
                return cert.NotAfter < DateTime.UtcNow.AddDays(30);
            }
            catch { return true; }
        }

        public static X509Certificate2 LoadOrCreate()
        {
            if (!NeedsRenewal())
            {
                try { return new X509Certificate2(CertPath, CertPasswd, X509KeyStorageFlags.Exportable); }
                catch { }
            }
            return Generate();
        }

        public static X509Certificate2 Generate()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CertPath));

            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(
                "CN=UnityMobileBridge,O=Unity,C=US",
                rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            req.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(
                new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));

            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            req.CertificateExtensions.Add(san.Build());

            var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
            var notAfter  = DateTimeOffset.UtcNow.AddDays(ValidDays);
            var cert = req.CreateSelfSigned(notBefore, notAfter);

            File.WriteAllBytes(CertPath, cert.Export(X509ContentType.Pfx, CertPasswd));
            Debug.Log($"[MobileBridge] Certificate generated: {CertPath}");
            return new X509Certificate2(CertPath, CertPasswd, X509KeyStorageFlags.Exportable);
        }
    }
}
