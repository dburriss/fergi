module Fergi.Cli.Program

[<EntryPoint>]
let main (argv: string[]) : int = Cli.run (List.ofArray argv) Io.real
