using System.Text;
using System.Xml;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol.Diagrams;

namespace KiCad.Automation.Mcp;

public static class RecursiveFileCommand
{
    public static async Task<int> RunAsync(TextReader input, TextWriter output, CancellationToken token)
    {
        RecursiveFileResult result;
        try
        {
            string json = await input.ReadToEndAsync(token);
            result = await RecursiveEditorFiles.ExecuteAsync(RecursiveFileRequest.Parser.ParseJson(json), token);
        }
        catch (OperationCanceledException)
        { result = new() { ErrorCode = "cancelled", ErrorMessage = "The diagram read was cancelled; no saved or draft content was changed." }; }
        catch (Exception error) when (error is AutomationException or IOException or UnauthorizedAccessException
            or InvalidJsonException or InvalidProtocolBufferException or XmlException or DecoderFallbackException or InvalidOperationException)
        {
            result = new() { ErrorCode = error is AutomationException a ? a.Code : "diagram_file_error", ErrorMessage = error.Message };
        }
        await output.WriteLineAsync(JsonFormatter.Default.Format(result));
        return result.Success ? 0 : 1;
    }
}
