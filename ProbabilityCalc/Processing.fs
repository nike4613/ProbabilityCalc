﻿
module Processing

open Prob
open FSharp.Data
open FSharp.Collections

type OutputCsv = CsvProvider<HasHeaders = false, Schema = "Name (string),Value (int),Occurences (int),Probability (float)">

module private Internal =
    type EvaluatedState<'v> = 
        { dieValues: Map<Die, int>
        ; realValue: 'v }

    let stateWithValue value = { dieValues = Map.empty; realValue = value }
    let copyWithValue state value = { dieValues = state.dieValues ; realValue = value }
    
    let modify op s = copyWithValue s (op s.realValue)

    let matchingDieValueFilter (a, b) = 
        a.dieValues
        |> Map.toSeq
        |> Seq.forall (fun (k, v) -> 
            match Map.tryFind k b.dieValues with
            | Some(bv) -> v = bv
            | None -> true)

    let mergeStates mergeVal (a, b) =
        { dieValues = Seq.concat [a.dieValues |> Map.toSeq ; b.dieValues |> Map.toSeq]
                        |> Seq.distinctBy fst
                        |> Map
        ; realValue = mergeVal a.realValue b.realValue }
        
    let binop eval op a b = 
        Seq.allPairs (eval a) (eval b) 
        |> Seq.filter matchingDieValueFilter
        |> Seq.map (mergeStates op)

    let mapModify op i = Seq.map (modify (fun v -> op v i))

    let evalCondition evalv a b conds =
        conds
        |> Seq.map (fun s -> s, if s.realValue then evalv a else evalv b)
        |> Seq.map (fun (s1, b) -> b |> Seq.map (fun s2 -> s1, s2))
        |> Seq.concat
        |> Seq.filter matchingDieValueFilter
        |> Seq.map (mergeStates (fun _ b -> b))

    let rec evaluateStates value =
        match value with
        | Number n -> stateWithValue n |> Seq.singleton
        | DieValue d -> seq { for i in 1..d.Size -> { dieValues = Map.add d i Map.empty; realValue = i } }
        | Sum(a, b) -> binop evaluateStates (+) a b
        | Difference(a, b) -> binop evaluateStates (-) a b
        | Multiply(a, b) -> binop evaluateStates (*) a b
        | Divide(a, b) -> binop evaluateStates (/) a b
        | Condition(cond, a, b) -> evaluateBools cond |> evalCondition evaluateStates a b
    and evaluateBools value =
        match value with
        | Literal b -> stateWithValue b |> Seq.singleton
        | Equals(v, i) -> evaluateStates v |> mapModify (=) i
        | NotEquals(v, i) -> evaluateStates v |> mapModify (<>) i
        | GreaterThan(v, i) -> evaluateStates v |> mapModify (>) i
        | LessThan(v, i) -> evaluateStates v |> mapModify (<) i
        | GreaterThanEqual(v, i) -> evaluateStates v |> mapModify (>=) i
        | LessThanEqual(v, i) -> evaluateStates v |> mapModify (<=) i
        | BoolNot v -> evaluateBools v |> Seq.map (modify not)
        | BoolAnd(a, b) -> binop evaluateBools (&&) a b
        | BoolOr(a, b) -> binop evaluateBools (||) a b
        | BoolCondition(cond, a, b) -> evaluateBools cond |> evalCondition evaluateBools a b

    let processWithName name prob : OutputCsv.Row seq = Seq.empty

let private makeCsv data = new OutputCsv(data)

let private processOneImpl i (data: OutputValue) =
    match data with
    | NamedOutput(name, value) -> Internal.processWithName name value
    | UnnamedOutput(value) -> Internal.processWithName (sprintf "output %o" i) value

let private processManyImpl (data: OutputValue seq) =
    data |> Seq.mapi processOneImpl |> Seq.concat

let processOne = processOneImpl 1 >> makeCsv
let processMany : OutputValue seq -> OutputCsv = processManyImpl >> makeCsv
