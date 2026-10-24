using System.Text.RegularExpressions;
using Govor.Application.Authentication.Exceptions;
using Govor.Domain.Common.Constants;
using Govor.Domain.Common;
using Microsoft.Extensions.Configuration;
using SmartRes;

namespace Govor.Application.Infrastructure.Validators;

public class UsernameValidator : IUsernameValidator
{
    private const string ErrorCode = nameof(InvalidUsernameException);
    
    private readonly Regex _usernameRegex = new(@"^[А-Яа-яЁё]+[А-Яа-яЁё0-9]*$", RegexOptions.Compiled);
    
    private readonly HashSet<string> _blockedExact;
    private readonly List<string> _blockedContains;
    private readonly HashSet<string> _reserved;

    public UsernameValidator(IConfiguration config)
    {
        _blockedExact = config.GetSection("UsernameModeration:BlockedExact")
                            .Get<string[]>()?
                            .Select(Normalize)
                            .ToHashSet()
                        ?? throw new InvalidOperationException("BlockedExact not set");

        _blockedContains = config
                               .GetSection("UsernameModeration:BlockedContains")
                               .Get<string[]>()?
                               .Select(Normalize)
                               .ToList()
                           ?? throw new InvalidOperationException("BlockedContains not set");

        _reserved = config
                        .GetSection("UsernameModeration:Reserved")
                        .Get<string[]>()?
                        .Select(Normalize)
                        .ToHashSet()
                    ?? throw new InvalidOperationException("Reserved not set");
    }
    
    public Result<Unit, Error> Validate(string username)
    {
        if (string.IsNullOrEmpty(username) || username.Length < UserConstants.MIN_LENGHT_OF_NAME || username.Length > UserConstants.MAX_LENGHT_OF_NAME)
        {
            return Result.Failure(Error.Validation(
                ErrorCode, 
                $"Username must be between {UserConstants.MIN_LENGHT_OF_NAME} and {UserConstants.MAX_LENGHT_OF_NAME} characters.")
            );
        }
        
        if (!_usernameRegex.IsMatch(username))
        {
            return Result.Failure(Error.Validation(
                ErrorCode, 
                "The username must be in Cyrillic and start with a letter.")
            );
        }

        if (Regex.IsMatch(username, @"(.)\1{4,}"))
        {
            return Result.Failure(Error.Validation(
                ErrorCode, 
                "Too many repeating characters.")
            );
        }
        
        var normalized = Normalize(username);
        
        if (_reserved.Contains(normalized))
        {
            return Result.Failure(Error.Validation(
                ErrorCode, 
                "This username is reserved.")
            );
        }
        
        if (_blockedExact.Contains(normalized))
        {
            return Result.Failure(Error.Validation(
                ErrorCode, 
                "This username is not allowed.")
            );
        }
        
        foreach (var banned in _blockedContains)
        {
            if (normalized.Contains(banned))
            {
                return Result.Failure(Error.Validation(
                    ErrorCode, 
                    "Username contains prohibited content.")
                );
            }
        }
        
        return Result.Success();
    }

    public bool TryValidate(string username)
    {
        return Validate(username).IsSuccess;
    }
        
    private static string Normalize(string username)
    {
        return username
            .ToLower()
            .Replace("0", "о")
            .Replace("1", "и")
            .Replace("3", "е")
            .Replace("4", "а")
            .Replace("6", "б")
            .Replace("8", "в");
    }
}
