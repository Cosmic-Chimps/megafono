/// Checks on shipped files rather than code: the documented privacy limit and the sample's integration size
module Megafono.Tests.RepositoryTests

open System.Diagnostics
open System.IO
open Xunit

/// The repository root, found from the test assembly by walking up to `Megafono.slnx`
let private root =
    let rec up (dir: DirectoryInfo) =
        if isNull dir then failwith "Megafono.slnx not found above the test assembly"
        elif File.Exists(Path.Combine(dir.FullName, "Megafono.slnx")) then dir.FullName
        else up dir.Parent

    up (DirectoryInfo(System.AppContext.BaseDirectory))

[<Fact>]
let ``FR-065 the operating docs and the README state that followers-only is a request, not a guarantee`` () =
    for file in [ "docs/operating.md"; "README.md" ] do
        let text = File.ReadAllText(Path.Combine(root, file))
        Assert.Contains("is a request, not a guarantee", text)

[<Fact>]
let ``SC-007 the sample app's integration code stays under 100 lines`` () =
    let script = Path.Combine(root, "scripts", "count-integration-lines.sh")
    let sample = Path.Combine(root, "samples", "SampleApp")
    let info = ProcessStartInfo("bash", $"\"{script}\" \"{sample}\"")
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    use proc = Process.Start info
    let output = proc.StandardOutput.ReadToEnd()
    proc.WaitForExit()
    Assert.True((proc.ExitCode = 0), $"count-integration-lines.sh exited {proc.ExitCode}: {output}")
    Assert.True(int (output.Trim()) < 100)
