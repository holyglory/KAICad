using System.Xml;
using System.Text;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol.Structural;

namespace KiCad.Automation.Mcp;

public static class StructuralFileCommand
{
    public static async Task<int> RunAsync(TextReader input, TextWriter output, CancellationToken token)
    {
        StructuralFileResult result;
        try
        {
            string json = await input.ReadToEndAsync(token);
            var request = StructuralFileRequest.Parser.ParseJson(json);
            result = new() { Success = true, Document = await StructuralEditorFiles.ExecuteAsync(request, token) };
        }
        catch (Exception error) when (error is AutomationException or IOException or UnauthorizedAccessException
            or InvalidJsonException or InvalidProtocolBufferException or XmlException or DecoderFallbackException)
        {
            result = new() { ErrorCode = error is AutomationException a ? a.Code : "structural_file_error", ErrorMessage = error.Message };
        }
        await output.WriteLineAsync(JsonFormatter.Default.Format(result));
        return result.Success ? 0 : 1;
    }
}
