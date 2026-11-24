namespace LibraryMI.ViewModels;

public static class Init
{
    public static IFileTransferService GetService()
    {
        return new FileTransferService();
    }
}

internal class FileTransferService : IFileTransferService
{
}

public interface IFileTransferService
{
}