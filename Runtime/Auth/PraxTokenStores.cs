using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Praxsuite
{
    /// <summary>
    /// Keeps the session in memory only. The player signs in again after every app restart.
    /// The most conservative option, and the right default for a shared or kiosk machine.
    /// </summary>
    public class PraxMemoryTokenStore : IPraxTokenStore
    {
        private PraxSession _session;

        public PraxSession Load() => _session;
        public void Save(PraxSession session) => _session = session;
        public void Clear() => _session = null;
    }

    /// <summary>
    /// Persists the session to an AES-256-CBC encrypted file under
    /// <c>Application.persistentDataPath</c>, with a key derived from device and application
    /// identifiers via PBKDF2.
    ///
    /// What this defends against: another application on the same device reading the file,
    /// a player casually opening the save folder, a session file copied to a different
    /// device (the derived key will not match, so it fails closed and the player re-signs in).
    ///
    /// What it does NOT defend against: the owner of the device. The decryption key is
    /// derived from values present on that device, so anyone who can attach a debugger to
    /// your process or dump its memory can recover the token. This is unavoidable for any
    /// client-side credential store on hardware the attacker controls; every client SDK has
    /// exactly the same ceiling.
    ///
    /// The security conclusion is therefore not "store tokens better", it is "do not let a
    /// stolen player session be worth stealing": keep authority server-side, give the
    /// client read-only scopes, and route currency, inventory grants and score submission
    /// through gateway endpoints. See docs/security.md.
    /// </summary>
    public class PraxEncryptedFileTokenStore : IPraxTokenStore
    {
        private const int SaltSize = 16;
        private const int IvSize = 16;
        private const int KeySize = 32;
        private const int Iterations = 100_000;
        private const byte FormatVersion = 1;

        private readonly string _path;
        private PraxSession _cached;
        private bool _loaded;

        public PraxEncryptedFileTokenStore(string workspaceId)
        {
            // One file per workspace so switching workspaces in the editor cannot cross
            // sessions, and so an unparseable file for one never breaks the other.
            var name = "praxsuite_" + Sanitize(workspaceId) + ".session";
            _path = Path.Combine(Application.persistentDataPath, name);
        }

        public PraxSession Load()
        {
            if (_loaded) return _cached;
            _loaded = true;

            try
            {
                if (!File.Exists(_path)) return null;

                var blob = File.ReadAllBytes(_path);
                if (blob.Length < 1 + SaltSize + IvSize + 1) return null;
                if (blob[0] != FormatVersion)
                {
                    PraxLog.Info("Stored session uses an older format; discarding it.");
                    Clear();
                    return null;
                }

                var salt = new byte[SaltSize];
                var iv = new byte[IvSize];
                Buffer.BlockCopy(blob, 1, salt, 0, SaltSize);
                Buffer.BlockCopy(blob, 1 + SaltSize, iv, 0, IvSize);

                var cipherLength = blob.Length - 1 - SaltSize - IvSize;
                var cipher = new byte[cipherLength];
                Buffer.BlockCopy(blob, 1 + SaltSize + IvSize, cipher, 0, cipherLength);

                var json = Decrypt(cipher, DeriveKey(salt), iv);
                _cached = JsonUtility.FromJson<PraxSession>(json);
                return _cached;
            }
            catch (Exception ex)
            {
                // A session that will not decrypt is not an error worth surfacing: the file
                // was written on other hardware, or the app identifier changed. Fail closed
                // and let the player sign in again.
                PraxLog.Info("Could not read the stored session (" + ex.GetType().Name +
                             "); the player will need to sign in again.");
                TryDelete();
                return null;
            }
        }

        public void Save(PraxSession session)
        {
            _cached = session;
            _loaded = true;

            if (session == null) { Clear(); return; }

            try
            {
                var salt = RandomBytes(SaltSize);
                var iv = RandomBytes(IvSize);
                var cipher = Encrypt(JsonUtility.ToJson(session), DeriveKey(salt), iv);

                var blob = new byte[1 + SaltSize + IvSize + cipher.Length];
                blob[0] = FormatVersion;
                Buffer.BlockCopy(salt, 0, blob, 1, SaltSize);
                Buffer.BlockCopy(iv, 0, blob, 1 + SaltSize, IvSize);
                Buffer.BlockCopy(cipher, 0, blob, 1 + SaltSize + IvSize, cipher.Length);

                // Write then move so a crash mid-write cannot leave a truncated session file.
                var temp = _path + ".tmp";
                File.WriteAllBytes(temp, blob);
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(temp, _path);
            }
            catch (Exception ex)
            {
                // Losing persistence degrades to memory-only, which is safe. Do not throw
                // out of a successful login just because the disk refused us.
                PraxLog.Warn("Could not persist the session: " + ex.Message +
                             ". It will stay in memory for this run only.");
            }
        }

        public void Clear()
        {
            _cached = null;
            _loaded = true;
            TryDelete();
        }

        private void TryDelete()
        {
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
            }
            catch (Exception ex)
            {
                PraxLog.Warn("Could not delete the stored session file: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ crypto

        private static byte[] DeriveKey(byte[] salt)
        {
            // deviceUniqueIdentifier binds the file to this device; the bundle identifier
            // keeps two apps on the same device from reading each other's sessions.
            var material = SystemInfo.deviceUniqueIdentifier + "|" + Application.identifier +
                           "|praxsuite-session-v1";

            using (var kdf = new Rfc2898DeriveBytes(material, salt, Iterations, HashAlgorithmName.SHA256))
                return kdf.GetBytes(KeySize);
        }

        private static byte[] Encrypt(string plaintext, byte[] key, byte[] iv)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var encryptor = aes.CreateEncryptor())
                {
                    var bytes = Encoding.UTF8.GetBytes(plaintext);
                    return encryptor.TransformFinalBlock(bytes, 0, bytes.Length);
                }
            }
        }

        private static string Decrypt(byte[] cipher, byte[] key, byte[] iv)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var decryptor = aes.CreateDecryptor())
                    return Encoding.UTF8.GetString(decryptor.TransformFinalBlock(cipher, 0, cipher.Length));
            }
        }

        private static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return bytes;
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "default";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                if (char.IsLetterOrDigit(c) || c == '-') sb.Append(c);
            return sb.Length > 0 ? sb.ToString() : "default";
        }
    }
}
