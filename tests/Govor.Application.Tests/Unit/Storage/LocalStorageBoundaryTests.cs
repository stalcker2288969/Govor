namespace Govor.Application.Tests.Unit.Storage;

[TestFixture]
public class LocalStorageBoundaryTests
{
    private string _root = null!;
    private LocalStorageService _service = null!;
    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "govor-storage-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _service = new(_root);
    }
    [TearDown]
    public void TearDown()
    {
        var resolved = Path.GetFullPath(_root);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("govor-storage-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected temporary storage path.");
        Directory.Delete(resolved, recursive: true);
    }

    [TestCase("../uploads-sibling/secret.bin")]
    [TestCase("../outside.bin")]
    public async Task TraversalCannotReadOrDeleteFilesOutsideUploadRoot(string relative)
    {
        var target = Path.GetFullPath(Path.Combine(_root, "uploads", relative));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "secret");
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.LoadAsync(relative));
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.RemoveAsync(relative));
        Assert.That(await File.ReadAllTextAsync(target), Is.EqualTo("secret"));
    }

    [Test]
    public async Task AbsolutePathsAreRejectedEvenInsideStorage()
    {
        var absolute = Path.Combine(_root, "uploads", "absolute.bin");
        await File.WriteAllTextAsync(absolute, "secret");
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.LoadAsync(absolute));
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.RemoveAsync(absolute));
        Assert.That(File.Exists(absolute), Is.True);
    }

    [Test]
    public async Task ReadOnlyFileCanBeDeletedAndRepeatedDeleteIsANoOp()
    {
        var path = await _service.SaveAsync(new byte[] { 1, 2 }, "../file.bin");
        var fullPath = Path.Combine(_root, "uploads", path);
        File.SetAttributes(fullPath, File.GetAttributes(fullPath) | FileAttributes.ReadOnly);
        await _service.RemoveAsync(path);
        await _service.RemoveAsync(path);
        Assert.That(File.Exists(fullPath), Is.False);
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase(null)]
    public void InvalidPathsAreRejected(string? path)
    {
        Assert.ThrowsAsync<ArgumentException>(() => _service.LoadAsync(path!));
        Assert.ThrowsAsync<ArgumentException>(() => _service.RemoveAsync(path!));
    }

    [Test]
    public async Task ReusingExistingRootIsSafeAndMissingLoadIsReported()
    {
        _service = new(_root);
        Assert.ThrowsAsync<FileNotFoundException>(() => _service.LoadAsync("missing.bin"));
        var relative = await _service.SaveAsync(new byte[] { 3, 4 }, "same.bin");
        await using var stream = await _service.LoadAsync(relative);
        Assert.That(stream.CanRead, Is.True);
        Assert.That(stream.Length, Is.EqualTo(2));
    }
}

