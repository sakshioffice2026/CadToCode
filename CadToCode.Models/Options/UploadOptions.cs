namespace CadToCode.Models.Options;

public class UploadOptions
{
    public const string SectionName = "UploadOptions";

    public string[] AllowedExtensions { get; set; } = { ".dxf", ".png", ".jpg", ".jpeg" };
    public int MaxFileSizeMb { get; set; } = 20;
    public string StorageRoot { get; set; } = string.Empty;
}