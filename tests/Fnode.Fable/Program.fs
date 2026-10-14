module Program

open System

[<EntryPoint>]
let main _ =
    let mutable passed = 0
    let failures = ResizeArray<string * string * string>()
    let perProject = Collections.Generic.Dictionary<string, int * int>()
    let order = ResizeArray<string>()

    for (project, name, run) in Runner.all do
        if not (perProject.ContainsKey project) then
            perProject.[project] <- (0, 0)
            order.Add project
        let (p, f) = perProject.[project]
        try
            run ()
            passed <- passed + 1
            perProject.[project] <- (p + 1, f)
        with e ->
            failures.Add((project, name, e.Message))
            perProject.[project] <- (p, f + 1)

    for (project, name, message) in failures do
        printfn "FAILED: [%s] %s\n    %s" project name (message.Replace("\n", "\n    "))

    for project in order do
        let (p, f) = perProject.[project]
        printfn "  %s: %d passed, %d failed" project p f

    printfn "  property cases generated: %d (at most %d per property)" FsCheck.Xunit.Check.casesRun FsCheck.Xunit.Check.maxCases
    printfn "Fable run: %d passed, %d failed, %d total" passed failures.Count (passed + failures.Count)
    if failures.Count = 0 then 0 else 1
