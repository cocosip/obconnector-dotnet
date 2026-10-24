using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OceanBaseConnector
{
    /// <summary>Immutable authentication material. Increase Version whenever user/passwords
    /// change. Versions are ordered within a live pool; same-version expiry extensions do not
    /// extend sessions already authenticated. Passwords are never included in ToString.</summary>
    public sealed class OceanBaseCredential
    {
        public OceanBaseCredential(string userId, string password, long version,
            DateTimeOffset? expiresAt = null, IReadOnlyDictionary<int, string>? additionalPasswords = null)
        {
            if (string.IsNullOrEmpty(userId) || userId.IndexOf('\0') >= 0) throw new ArgumentException("A nonempty user ID without NUL is required.", nameof(userId));
            if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
            UserId = userId; Password = password ?? throw new ArgumentNullException(nameof(password));
            Version = version; ExpiresAt = expiresAt;
            var passwords = new Dictionary<int, string>();
            if (additionalPasswords != null)
                foreach (var pair in additionalPasswords)
                {
                    if (pair.Key < 2) throw new ArgumentException("Additional passwords use numbers >= 2.", nameof(additionalPasswords));
                    passwords.Add(pair.Key, pair.Value ?? throw new ArgumentException("Additional passwords cannot be null.", nameof(additionalPasswords)));
                }
            AdditionalPasswords = new ReadOnlyDictionary<int, string>(passwords);
        }
        public string UserId { get; }
        public string Password { get; }
        public long Version { get; }
        public DateTimeOffset? ExpiresAt { get; }
        public IReadOnlyDictionary<int, string> AdditionalPasswords { get; }
        public override string ToString() => nameof(OceanBaseCredential);
        internal bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value <= DateTimeOffset.UtcNow;
        internal void EnsureValid() { if (IsExpired) throw new OceanBaseException("The supplied credential has expired."); }
        internal bool SameAuthentication(OceanBaseCredential other)
        {
            if (UserId != other.UserId || Password != other.Password || AdditionalPasswords.Count != other.AdditionalPasswords.Count) return false;
            foreach (var pair in AdditionalPasswords)
                if (!other.AdditionalPasswords.TryGetValue(pair.Key, out var value) || value != pair.Value) return false;
            return true;
        }
    }
}
