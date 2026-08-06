using System.Security.Cryptography;
using System.Text;
using CSSApps.Shared.Contracts.Primitives;
using Microsoft.Extensions.Logging;

namespace CSSApps.Users.Application.Services.Auth.Password;

public sealed class PasswordService
{
    private readonly ILogger<PasswordService> _logger;
    private const int SaltSize = 32; // 256 bits
    private const int HashSize = 32; // 256 bits
    private const int Iterations = 600000;
    private const string Format = "pbkdf2-sha256";

    public PasswordService(ILogger<PasswordService> logger)
    {
        _logger = logger;
    }

    public Result<string> HashPassword(string password)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(password))
                return new ResultError("Password cannot be empty");

            // Generate a salt
            byte[] salt = new byte[SaltSize];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }

            // Hash the password
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                Iterations,
                HashAlgorithmName.SHA256,
                HashSize);

            return $"{Format}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.auth] Failed to hash password");
            return new ResultError($"Failed to hash password: {ex.Message}");
        }
    }

    public Result<bool> VerifyPassword(string password, string hashedPassword)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(password))
                return new ResultError("Password cannot be empty");

            if (!TryReadHash(hashedPassword, out var iterations, out var salt, out var hash))
                return new ResultError("Invalid hash format");

            // Hash the provided password with the same salt
            byte[] testHash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                HashSize);

            // Compare hashes
            return CryptographicOperations.FixedTimeEquals(hash, testHash);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[users.auth] Failed to verify password");
            return new ResultError($"Failed to verify password: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns whether a stored value is a password credential this version can verify.
    /// Unsupported pre-release hashes are treated as unset so an authenticated user can replace them.
    /// </summary>
    public static bool IsSupportedHash(string? hashedPassword) =>
        TryReadHash(hashedPassword, out _, out _, out _);

    public static Result<bool> IsPasswordStrong(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return new ResultError("Password cannot be empty");

        var issues = new List<string>();

        if (password.Length < 12)
            issues.Add("Password must be at least 12 characters long");

        if (!password.Any(char.IsUpper))
            issues.Add("Password must contain at least one uppercase letter");

        if (!password.Any(char.IsLower))
            issues.Add("Password must contain at least one lowercase letter");

        if (!password.Any(char.IsDigit))
            issues.Add("Password must contain at least one digit");

        if (!password.Any(ch => !char.IsLetterOrDigit(ch)))
            issues.Add("Password must contain at least one special character");

        if (issues.Count > 0)
        {
            return new ResultError($"Password strength validation failed: {string.Join("; ", issues)}");
        }

        return true;
    }

    private static bool TryReadHash(
        string? hashedPassword,
        out int iterations,
        out byte[] salt,
        out byte[] hash)
    {
        iterations = 0;
        salt = [];
        hash = [];
        if (string.IsNullOrWhiteSpace(hashedPassword)) return false;

        var fields = hashedPassword.Split('$');
        if (fields.Length != 4 ||
            !string.Equals(fields[0], Format, StringComparison.Ordinal) ||
            !int.TryParse(fields[1], out iterations) ||
            iterations < Iterations)
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(fields[2]);
            hash = Convert.FromBase64String(fields[3]);
            return salt.Length == SaltSize && hash.Length == HashSize;
        }
        catch (FormatException)
        {
            return false;
        }
    }

}
