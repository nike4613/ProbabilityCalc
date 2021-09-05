﻿
module Dice

open DiceTracker

let rollsingle skill =
    prob {
        let! roll = d 12
        let! modified = roll + (Arg 0)
        return! condb (roll =. !>12)
            (Literal true)
            (condb (roll =. !>1)
                (Literal false)
                (modified >. !>9))
    } |> funcnb "rollsingle" [skill]

let roll skill count =
    prob {
        return! seq { for _ in 1..count -> rollsingle (Arg 0) }
        |> Seq.map toProb |> Seq.reduce (+)
    } |> funcn "roll" [skill]

let rolldiff count skill diff = 
    prob {
        let pityDice =
            if diff > count then
                seq { for _ in 1..(diff - count) -> d 12 =. !>12 }
                |> Seq.map toProb |> Seq.reduce (+)
            else !>0
        return! ((roll (Arg 0) count) + pityDice >=. !>diff)
    } |> funcnb "rolldiff" [skill]

let result =
    seq {
        for attr in 1..8 do
            for skill in 0..5 do
                yield rolldiff attr !>skill 2 |> toProb 
                |> outputName $"attr {attr} skill {skill}"
    }

[<EntryPoint>]
let main argv =
    printfn ""
    let results = result |> Processing.processMany
    printfn $"{results}"
    0 // return an integer exit code