﻿namespace DiceTracker.Website

open System
open System.IO
open System.Diagnostics
open System.Reflection
open FSharp.Compiler
open FSharp.Compiler.Text
open FSharp.Compiler.Symbols
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.CodeAnalysis
open Microsoft.JSInterop

type CompilerStatus =
    | Standby
    | Running
    | Failed of Choice<FSharpDiagnostic[], string list>
    | Succeeded of Assembly * result:MemberInfo option * FSharpDiagnostic[]

type FileResults =
    {
        parse: FSharpParseFileResults
        check: FSharpCheckFileResults
    }
module FileResults = let ofRes(parse, check) = { parse = parse ; check = check }

type Compiler =
    {
        checker: FSharpChecker
        options: FSharpProjectOptions
        checkResults: FSharpCheckProjectResults
        mainFile: FileResults
        sequence: int
        status: CompilerStatus
    }

module Compiler =

    // force the F# compiler to use /tmp/ as its bindir
    do Environment.SetEnvironmentVariable("FSHARP_COMPILER_BIN", "/tmp/")
    
    let project = "/tmp/input.fsproj"
    let inFile = "/tmp/input.fs"
    let outFile = "/tmp/output.dll"

    let basicDependencies = 
        [
            "FSharp.Core"
            "mscorlib"
            "netstandard"
            "System"
            "System.Core"
            "System.IO"
            "System.Runtime"
        ]

    let mkOptions (checker: FSharpChecker) outFile =
        checker.GetProjectOptionsFromCommandLineArgs(project, [|
            yield! [|
                "--simpleresolution"
                "--optimize-"
                "--noframework"
                "--fullpaths"
                "--warn:3"
                "--target:dll"
                inFile
            |]
            yield! basicDependencies |> List.toArray |> Array.map (fun s -> $"-r:/tmp/{s}.dll")
            yield "-o:" + outFile
        |])

    let create source = async {
        let checker = FSharpChecker.Create(keepAssemblyContents = true, suggestNamesForErrors = true)
        let options = mkOptions checker outFile
        File.WriteAllText(inFile, source)

        // start a check on creation so that we're ready to go when we finish initializing
        let! checkProjRes = checker.ParseAndCheckProject(options)
        let! (parseRes, checkRes) = checker.GetBackgroundCheckResultsForFileInProject(inFile, options)
        let! (diagnostics, code, assembly) = checker.CompileToDynamicAssembly([parseRes.ParseTree], "output", 
                                                basicDependencies, None, noframework = true)
        return {
            checker = checker
            options = options
            checkResults = checkProjRes
            mainFile = FileResults.ofRes(parseRes, checkRes)
            sequence = 0
            status = Standby
        }
    }

    let isFailure (errors: FSharpDiagnostic seq) =
        errors |> Seq.exists (fun x -> x.Severity = FSharpDiagnosticSeverity.Error)

    let resultExpectedTypeOne = "DiceTracker.Probability.OutputValue"
    let resultExpectedTypeMany = "System.Collections.Generic.IEnumerable<" + resultExpectedTypeOne + ">"

    let findResultMember (checkRes: FSharpCheckProjectResults) =
        match checkRes.AssemblySignature.FindEntityByPath ["Dice"] with
        | None -> None
        | Some ent ->
            ent.MembersFunctionsAndValues
            |> Seq.filter (fun v -> 
                v.IsValue &&
                v.LogicalName = "result" &&
                    let typen = v.FullType.Format(FSharpDisplayContext.Empty) 
                    typen = resultExpectedTypeOne || typen = resultExpectedTypeMany
            ) |> Seq.tryExactlyOne

    let checkDelay = Delayer(500) // the delayer to use for checking user input

open Compiler

type Compiler with
    member comp.Run (source: string) =
        { comp with status = Running},
        fun () -> async {
            let sw = Stopwatch.StartNew()
            let outfile = $"/tmp/out{comp.sequence}.dll"
            File.WriteAllText(inFile, source)

            let options = Compiler.mkOptions comp.checker outfile
            let! checkRes = comp.checker.ParseAndCheckProject(options)
            if checkRes.HasCriticalErrors then return { comp with status = Failed <| Choice1Of2 checkRes.Diagnostics } else
            
            match findResultMember checkRes with
            | None -> return { comp with status = Failed <| Choice2Of2 ["No result member found"] }
            | Some minfo ->

            let! parseResult = comp.checker.ParseFile(inFile, SourceText.ofString source, FSharpParsingOptions.Default, cache=false)
            let! errors, errCode, assembly = comp.checker.CompileToDynamicAssembly([parseResult.ParseTree], $"output{comp.sequence}", basicDependencies, None, noframework=true)
            sw.Stop()
            printfn "Compile took %A" sw.Elapsed

            if isFailure errors || errCode <> 0 then return { comp with status = Failed <| Choice1Of2 errors } else
            match assembly with
            | None -> return { comp with status = Failed <| Choice2Of2 ["No assembly returned despite the apparently successful compilation"] }
            | Some assembly ->

            let diceModule = assembly.GetType(minfo.DeclaringEntity 
                                                |> Option.map (fun e -> e.CompiledName)
                                                |> Option.orElse (Some "Dice")
                                                |> Option.get) |> Option.ofObj
            let resultMember = diceModule |> Option.map (fun m -> m.GetMember(minfo.CompiledName, BindingFlags.Static ||| BindingFlags.Public))
            let resultMember = resultMember |> Option.map Array.tryHead |> Option.flatten

            return 
                { comp with
                    sequence = comp.sequence + 1
                    status = Succeeded(assembly, resultMember, errors) }
        }