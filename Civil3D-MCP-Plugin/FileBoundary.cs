using System.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Civil3DMcpPlugin;

/// <summary>
/// Authoritative filesystem boundary for caller-supplied import and export paths.
/// Paths are canonicalized, restricted to configured roots, and checked against
/// a command-specific extension allowlist before Civil 3D or System.IO sees them.
/// </summary>
internal static class FileBoundary
{
  private const string SharedRootsVariable = "CIVIL3D_FILE_ROOTS";
  private const string ImportRootsVariable = "CIVIL3D_IMPORT_ROOTS";
  private const string ExportRootsVariable = "CIVIL3D_EXPORT_ROOTS";
  private const uint FileFlagBackupSemantics = 0x02000000;
  private const uint FileFlagOpenReparsePoint = 0x00200000;
  private const int FileAttributeTagInfoClass = 9;

  private static readonly Lazy<string[]> ImportRoots = new(() => LoadRoots(ImportRootsVariable));
  private static readonly Lazy<string[]> ExportRoots = new(() => LoadRoots(ExportRootsVariable));

  /// <summary>C222: the configured import roots (the rule profile is looked up in them).</summary>
  internal static IReadOnlyList<string> ImportRootPaths => ImportRoots.Value;

  public static string ResolveImportPath(string rawPath, params string[] allowedExtensions)
  {
    var path = ResolvePath(rawPath, ImportRoots.Value, "import", allowedExtensions);
    if (!File.Exists(path))
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Import file was not found: {path}");
    }

    return path;
  }

  public static string ResolveExportPath(
    string rawPath,
    bool overwrite,
    params string[] allowedExtensions)
  {
    var path = ResolvePath(rawPath, ExportRoots.Value, "export", allowedExtensions);
    if (File.Exists(path) && !overwrite)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.CONFLICT",
        $"Output file already exists: {path}. Set overwrite=true to replace it explicitly.");
    }

    return path;
  }

  /// <summary>
  /// C93/C94: a drawing to open or to read: inside the import or the export roots (a project drawing the plugin saved
  /// lives in an export root), on a local disk, existing.
  /// </summary>
  public static string ResolveLocalReadPath(string rawPath, params string[] allowedExtensions)
  {
    var roots = ImportRoots.Value.Concat(ExportRoots.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var path = ResolvePath(rawPath, roots, "source", allowedExtensions);
    RequireLocalDisk(path);
    if (!File.Exists(path))
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"File was not found: {path}");
    }

    return path;
  }

  /// <summary>
  /// C94 review: whether two existing paths are the same file (volume and file index), so a short 8.3 name or a hard
  /// link to the source is not taken for another file; false when either cannot be opened.
  /// </summary>
  public static bool SameFile(string first, string second)
  {
    using var a = CreateFileW(first, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, FileFlagBackupSemantics, IntPtr.Zero);
    using var b = CreateFileW(second, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, FileFlagBackupSemantics, IntPtr.Zero);
    if (a.IsInvalid || b.IsInvalid) return false;
    return GetFileInformationByHandle(a, out var x) && GetFileInformationByHandle(b, out var y)
      && x.VolumeSerialNumber == y.VolumeSerialNumber && x.FileIndexHigh == y.FileIndexHigh && x.FileIndexLow == y.FileIndexLow;
  }

  /// <summary>
  /// C93: why a drawing could not be opened for editing, null when it can: read-only on disk, or held by another
  /// program (another Civil 3D keeps an open drawing locked against writing). Probes by opening the file with write
  /// access and sharing, writing nothing; without this AutoCAD would ask in a dialog.
  /// </summary>
  public static string? WriteAccessProblem(string path)
  {
    try
    {
      if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) return "the file is read-only on disk";
      using var probe = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
      return null;
    }
    catch (IOException)
    {
      return "the file is open in another program (or another Civil 3D)";
    }
    catch (UnauthorizedAccessException)
    {
      return "there is no write access to the file";
    }
  }

  /// <summary>C94: an output file inside the export roots and on a local disk.</summary>
  public static string ResolveLocalExportPath(string rawPath, bool overwrite, params string[] allowedExtensions)
  {
    var path = ResolveExportPath(rawPath, overwrite, allowedExtensions);
    RequireLocalDisk(path);
    return path;
  }

  /// <summary>
  /// C93/C94: only files on the computer's own disks (policy: no network sources). Refuses UNC and device paths
  /// (\\server\share, \\?\, \\.\), network drives, drives that do not exist and SUBST drives (a drive letter whose DOS
  /// device is another path, \??\…, which may itself be a network share).
  /// </summary>
  public static void RequireLocalDisk(string path)
  {
    if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
    {
      throw new JsonRpcDispatchException("CIVIL3D.PATH_NOT_ALLOWED", $"Only paths on a local disk are allowed (no UNC or device paths): {path}");
    }

    var root = Path.GetPathRoot(path);
    if (root == null || root.Length < 2 || root[1] != ':' || !char.IsAsciiLetter(root[0]))
    {
      throw new JsonRpcDispatchException("CIVIL3D.PATH_NOT_ALLOWED", $"Only paths on a local disk with a drive letter are allowed: {path}");
    }

    DriveType driveType;
    try
    {
      driveType = new DriveInfo(root[..2]).DriveType;
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
    {
      throw new JsonRpcDispatchException("CIVIL3D.PATH_NOT_ALLOWED", $"The drive of the path could not be checked ({exception.Message}): {path}");
    }

    if (driveType is DriveType.Network or DriveType.NoRootDirectory or DriveType.Unknown)
    {
      throw new JsonRpcDispatchException("CIVIL3D.PATH_NOT_ALLOWED", $"Only paths on a local disk are allowed ({root[..2]} is {driveType}): {path}");
    }

    var target = new StringBuilder(1024);
    if (QueryDosDeviceW(root[..2], target, target.Capacity) != 0 && target.ToString().StartsWith(@"\??\", StringComparison.Ordinal))
    {
      throw new JsonRpcDispatchException("CIVIL3D.PATH_NOT_ALLOWED", $"SUBST drives are not allowed ({root[..2]} is {target}): {path}");
    }
  }

  /// <summary>
  /// C94: writes a file through a temporary file in the target folder and moves it into place (the export directory
  /// chain locked as for text files), so a failed write leaves no partial output and keeps an existing file.
  /// <paramref name="writeTemp"/> gets the temporary path (same extension as the target).
  /// </summary>
  public static string WriteFileAtomic(
    string rawPath,
    bool overwrite,
    Action<string> writeTemp,
    params string[] allowedExtensions)
  {
    var path = ResolveLocalExportPath(rawPath, overwrite, allowedExtensions);
    var directory = Path.GetDirectoryName(path)
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Output path must include a directory.");
    using var directoryLock = LockExportDirectoryChain(directory);

    var tempPath = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}.tmp{Path.GetExtension(path)}");
    try
    {
      writeTemp(tempPath);
      File.Move(tempPath, path, overwrite);
      return path;
    }
    catch (IOException) when (!overwrite && File.Exists(path))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.CONFLICT",
        $"Output file already exists: {path}. Set overwrite=true to replace it explicitly.");
    }
    catch (JsonRpcDispatchException)
    {
      throw;
    }
    catch (Exception exception)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.FILE_IO_ERROR",
        $"Unable to write output file '{path}': {exception.Message}");
    }
    finally
    {
      try
      {
        if (File.Exists(tempPath))
        {
          File.Delete(tempPath);
        }
      }
      catch
      {
        // Preserve the original operation result; the hidden temp file can be removed later.
      }
    }
  }

  public static string WriteAllTextAtomic(
    string rawPath,
    string content,
    Encoding encoding,
    bool overwrite,
    params string[] allowedExtensions)
  {
    var path = ResolveExportPath(rawPath, overwrite, allowedExtensions);
    var directory = Path.GetDirectoryName(path)
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Output path must include a directory.");
    using var directoryLock = LockExportDirectoryChain(directory);

    var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    try
    {
      File.WriteAllText(tempPath, content, encoding);
      File.Move(tempPath, path, overwrite);
      return path;
    }
    catch (IOException) when (!overwrite && File.Exists(path))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.CONFLICT",
        $"Output file already exists: {path}. Set overwrite=true to replace it explicitly.");
    }
    catch (JsonRpcDispatchException)
    {
      throw;
    }
    catch (Exception exception)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.FILE_IO_ERROR",
        $"Unable to write output file '{path}': {exception.Message}");
    }
    finally
    {
      try
      {
        if (File.Exists(tempPath))
        {
          File.Delete(tempPath);
        }
      }
      catch
      {
        // Preserve the original operation result. Stale temp files use a
        // hidden, collision-resistant name and can be removed later.
      }
    }
  }

  private static string ResolvePath(
    string rawPath,
    IReadOnlyCollection<string> roots,
    string operation,
    IReadOnlyCollection<string> allowedExtensions)
  {
    if (string.IsNullOrWhiteSpace(rawPath))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"A non-empty {operation} path is required.");
    }

    if (!Path.IsPathFullyQualified(rawPath))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"The {operation} path must be absolute: {rawPath}");
    }

    string canonicalPath;
    try
    {
      canonicalPath = Path.GetFullPath(rawPath);
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Invalid {operation} path: {exception.Message}");
    }

    var matchedRoot = roots.FirstOrDefault(root => IsWithinRoot(canonicalPath, root));
    if (matchedRoot == null)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.PATH_NOT_ALLOWED",
        $"The {operation} path is outside the configured roots: {canonicalPath}");
    }

    RejectReparsePointTraversal(canonicalPath, matchedRoot);

    var allowed = allowedExtensions
      .Select(NormalizeExtension)
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var extension = Path.GetExtension(canonicalPath);
    if (allowed.Count > 0 && !allowed.Contains(extension))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.FILE_TYPE_NOT_ALLOWED",
        $"Extension '{extension}' is not allowed for this operation. Allowed extensions: {string.Join(", ", allowed.Order())}.");
    }

    return canonicalPath;
  }

  private static string[] LoadRoots(string operationVariable)
  {
    var configured = Environment.GetEnvironmentVariable(operationVariable);
    if (string.IsNullOrWhiteSpace(configured))
    {
      configured = Environment.GetEnvironmentVariable(SharedRootsVariable);
    }

    var roots = SplitRoots(configured).ToList();
    if (roots.Count == 0)
    {
      var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
      if (!string.IsNullOrWhiteSpace(documents))
      {
        roots.Add(documents);
      }
    }

    return roots
      .Select(Path.GetFullPath)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();
  }

  private static IEnumerable<string> SplitRoots(string? configured)
  {
    if (string.IsNullOrWhiteSpace(configured))
    {
      yield break;
    }

    foreach (var root in configured.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
      if (!Path.IsPathFullyQualified(root))
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_CONFIGURATION",
          $"Configured filesystem root must be absolute: {root}");
      }

      yield return root;
    }
  }

  private static bool IsWithinRoot(string path, string root)
  {
    var relative = Path.GetRelativePath(root, path);
    return !Path.IsPathRooted(relative)
      && !string.Equals(relative, "..", StringComparison.Ordinal)
      && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
      && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
  }

  private static void RejectReparsePointTraversal(string path, string root)
  {
    var relative = Path.GetRelativePath(root, path);
    if (relative == ".")
    {
      return;
    }

    var current = root;
    foreach (var segment in relative.Split(
      [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
      StringSplitOptions.RemoveEmptyEntries))
    {
      current = Path.Combine(current, segment);
      if (!Directory.Exists(current) && !File.Exists(current))
      {
        break;
      }

      try
      {
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
          throw new JsonRpcDispatchException(
            "CIVIL3D.PATH_NOT_ALLOWED",
            $"Filesystem links and junctions are not allowed in caller-supplied paths: {current}");
        }
      }
      catch (JsonRpcDispatchException)
      {
        throw;
      }
      catch (Exception exception)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.FILE_IO_ERROR",
          $"Unable to validate filesystem path '{current}': {exception.Message}");
      }
    }
  }

  private static string NormalizeExtension(string extension) =>
    extension.StartsWith('.') ? extension : $".{extension}";

  private static DirectoryChainLock LockExportDirectoryChain(string directory)
  {
    var canonicalDirectory = Path.GetFullPath(directory);
    var matchedRoot = ExportRoots.Value
      .Where(root => IsWithinRoot(canonicalDirectory, root))
      .OrderByDescending(root => root.Length)
      .FirstOrDefault()
      ?? throw new JsonRpcDispatchException(
        "CIVIL3D.PATH_NOT_ALLOWED",
        $"The export path is outside the configured roots: {canonicalDirectory}");

    if (!Directory.Exists(matchedRoot))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_CONFIGURATION",
        $"Configured export root does not exist: {matchedRoot}");
    }

    var handles = new List<SafeFileHandle>();
    try
    {
      var current = Path.GetFullPath(matchedRoot);
      handles.Add(OpenLockedDirectory(current));
      var relative = Path.GetRelativePath(current, canonicalDirectory);
      if (relative != ".")
      {
        foreach (var segment in relative.Split(
          [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
          StringSplitOptions.RemoveEmptyEntries))
        {
          var next = Path.Combine(current, segment);
          if (!Directory.Exists(next))
          {
            // The parent handle excludes FILE_SHARE_DELETE, so it cannot be
            // swapped for a junction between creation and the next handle open.
            Directory.CreateDirectory(next);
          }
          handles.Add(OpenLockedDirectory(next));
          current = next;
        }
      }
      return new DirectoryChainLock(handles);
    }
    catch
    {
      foreach (var handle in handles) handle.Dispose();
      throw;
    }
  }

  private static SafeFileHandle OpenLockedDirectory(string path)
  {
    var handle = CreateFileW(
      path,
      0,
      FileShare.ReadWrite,
      IntPtr.Zero,
      FileMode.Open,
      FileFlagBackupSemantics | FileFlagOpenReparsePoint,
      IntPtr.Zero);
    if (handle.IsInvalid)
    {
      var error = new Win32Exception(Marshal.GetLastWin32Error());
      handle.Dispose();
      throw new JsonRpcDispatchException(
        "CIVIL3D.FILE_IO_ERROR",
        $"Unable to lock filesystem directory '{path}': {error.Message}");
    }

    if (!GetFileInformationByHandleEx(
      handle,
      FileAttributeTagInfoClass,
      out var information,
      (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
    {
      var error = new Win32Exception(Marshal.GetLastWin32Error());
      handle.Dispose();
      throw new JsonRpcDispatchException(
        "CIVIL3D.FILE_IO_ERROR",
        $"Unable to inspect filesystem directory '{path}': {error.Message}");
    }

    if ((information.FileAttributes & FileAttributes.ReparsePoint) != 0)
    {
      handle.Dispose();
      throw new JsonRpcDispatchException(
        "CIVIL3D.PATH_NOT_ALLOWED",
        $"Filesystem links and junctions are not allowed in caller-supplied paths: {path}");
    }
    return handle;
  }

  private sealed class DirectoryChainLock(List<SafeFileHandle> handles) : IDisposable
  {
    public void Dispose()
    {
      for (var index = handles.Count - 1; index >= 0; index--)
        handles[index].Dispose();
    }
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct FileAttributeTagInfo
  {
    public FileAttributes FileAttributes;
    public uint ReparseTag;
  }

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern SafeFileHandle CreateFileW(
    string fileName,
    uint desiredAccess,
    FileShare shareMode,
    IntPtr securityAttributes,
    FileMode creationDisposition,
    uint flagsAndAttributes,
    IntPtr templateFile);

  [StructLayout(LayoutKind.Sequential)]
  private struct ByHandleFileInformation
  {
    public uint FileAttributes;
    public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
    public uint VolumeSerialNumber;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint NumberOfLinks;
    public uint FileIndexHigh;
    public uint FileIndexLow;
  }

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern uint QueryDosDeviceW(string deviceName, StringBuilder targetPath, int maxLength);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetFileInformationByHandleEx(
    SafeFileHandle file,
    int fileInformationClass,
    out FileAttributeTagInfo fileInformation,
    uint bufferSize);
}
