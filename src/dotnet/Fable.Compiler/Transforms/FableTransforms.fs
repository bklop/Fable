module Fable.Transforms.FableTransforms

open Fable
open Fable.AST.Fable
open Microsoft.FSharp.Compiler.SourceCodeServices

// TODO: Use trampoline here?
let visit f e =
    match e with
    | IdentExpr _ | Import _ | Debugger _ -> e
    | Value kind ->
        match kind with
        | This _ | Null _ | UnitConstant
        | BoolConstant _ | CharConstant _ | StringConstant _
        | NumberConstant _ | RegexConstant _ | Enum _ -> e
        | NewOption(e, t) -> NewOption(Option.map f e, t) |> Value
        | NewTuple exprs -> NewTuple(List.map f exprs) |> Value
        | NewArray(kind, t) ->
            match kind with
            | ArrayValues exprs -> NewArray(ArrayValues(List.map f exprs), t) |> Value
            | ArrayAlloc e -> NewArray(ArrayAlloc(f e), t) |> Value
        | NewList(ht, t) ->
            let ht = ht |> Option.map (fun (h,t) -> f h, f t)
            NewList(ht, t) |> Value
        | NewRecord(exprs, ent, genArgs) ->
            NewRecord(List.map f exprs, ent, genArgs) |> Value
        | NewErasedUnion(e, genArgs) ->
            NewErasedUnion(f e, genArgs) |> Value
        | NewUnion(exprs, uci, ent, genArgs) ->
            NewUnion(List.map f exprs, uci, ent, genArgs) |> Value
    | Test(e, kind, r) -> Test(f e, kind, r)
    | Cast(e, t) -> Cast(f e, t)
    | Function(kind, body, name) -> Function(kind, f body, name)
    | ObjectExpr(members, t, baseCall) ->
        let baseCall = Option.map f baseCall
        let members = members |> List.map (fun (n,v,k) -> n, f v, k)
        ObjectExpr(members, t, baseCall)
    | Operation(kind, t, r) ->
        match kind with
        | CurriedApply(callee, args) ->
            Operation(CurriedApply(f callee, List.map f args), t, r)
        | Call(kind, info) ->
            let kind =
                match kind with
                | ConstructorCall e -> ConstructorCall(f e)
                | StaticCall e -> StaticCall(f e)
                | InstanceCall memb -> InstanceCall(Option.map f memb)
            let info = { info with ThisArg = Option.map f info.ThisArg
                                   Args = List.map f info.Args }
            Operation(Call(kind, info), t, r)
        | Emit(macro, info) ->
            let info = info |> Option.map (fun info ->
                { info with ThisArg = Option.map f info.ThisArg
                            Args = List.map f info.Args })
            Operation(Emit(macro, info), t, r)
        | UnaryOperation(operator, operand) ->
            Operation(UnaryOperation(operator, f operand), t, r)
        | BinaryOperation(op, left, right) ->
            Operation(BinaryOperation(op, f left, f right), t, r)
        | LogicalOperation(op, left, right) ->
            Operation(LogicalOperation(op, f left, f right), t, r)
    | Get(e, kind, t, r) ->
        match kind with
        | ListHead | ListTail | OptionValue | TupleGet _ | UnionTag _
        | UnionField _ | RecordGet _ -> Get(f e, kind, t, r)
        | ExprGet e2 -> Get(f e, ExprGet (f e2), t, r)
    | Throw(e, typ, r) -> Throw(f e, typ, r)
    | Sequential exprs -> Sequential(List.map f exprs)
    | Let(bs, body) ->
        let bs = bs |> List.map (fun (i,e) -> i, f e)
        Let(bs, f body)
    | IfThenElse(cond, thenExpr, elseExpr) ->
        IfThenElse(f cond, f thenExpr, f elseExpr)
    | Set(e, kind, v, r) ->
        match kind with
        | VarSet | RecordSet _ ->
            Set(f e, kind, f v, r)
        | ExprSet e2 -> Set(f e, ExprSet (f e2), f v, r)
    | Loop (kind, r) ->
        match kind with
        | While(e1, e2) -> Loop(While(f e1, f e2), r)
        | For(i, e1, e2, e3, up) -> Loop(For(i, f e1, f e2, f e3, up), r)
    | TryCatch(body, catch, finalizer) ->
        TryCatch(f body,
                 Option.map (fun (i, e) -> i, f e) catch,
                 Option.map f finalizer)
    | DecisionTree(expr, targets) ->
        let targets = targets |> List.map (fun (idents, v) -> idents, f v)
        DecisionTree(f expr, targets)
    | DecisionTreeSuccess(idx, boundValues, t) ->
        DecisionTreeSuccess(idx, List.map f boundValues, t)

let rec visitFromInsideOut f e =
    visit (visitFromInsideOut f) e |> f

let rec visitFromOutsideIn (f: Expr->Expr option) e =
    match f e with
    | Some e -> e
    | None ->
        visit (visitFromOutsideIn f) e

let getSubExpressions = function
    | IdentExpr _ | Import _ | Debugger _ -> []
    | Value kind ->
        match kind with
        | This _ | Null _ | UnitConstant
        | BoolConstant _ | CharConstant _ | StringConstant _
        | NumberConstant _ | RegexConstant _ | Enum _ -> []
        | NewOption(e, _) -> Option.toList e
        | NewTuple exprs -> exprs
        | NewArray(kind, _) ->
            match kind with
            | ArrayValues exprs -> exprs
            | ArrayAlloc e -> [e]
        | NewList(ht, _) ->
            match ht with Some(h,t) -> [h;t] | None -> []
        | NewRecord(exprs, _, _) -> exprs
        | NewErasedUnion(e, _) -> [e]
        | NewUnion(exprs, _, _, _) -> exprs
    | Test(e, _, _) -> [e]
    | Cast(e, _) -> [e]
    | Function(_, body, _) -> [body]
    | ObjectExpr(members, _, baseCall) ->
        let members = members |> List.map (fun (_,v,_) -> v)
        match baseCall with Some b -> b::members | None -> members
    | Operation(kind, _, _) ->
        match kind with
        | CurriedApply(callee, args) -> callee::args
        | Call(kind, info) ->
            let e1 =
                match kind with
                | ConstructorCall e -> [e]
                | StaticCall e -> [e]
                | InstanceCall memb -> Option.toList memb
            e1 @ (Option.toList info.ThisArg) @ info.Args
        | Emit(_, info) ->
            match info with Some info -> (Option.toList info.ThisArg) @ info.Args | None -> []
        | UnaryOperation(_, operand) -> [operand]
        | BinaryOperation(_, left, right) -> [left; right]
        | LogicalOperation(_, left, right) -> [left; right]
    | Get(e, kind, _, _) ->
        match kind with
        | ListHead | ListTail | OptionValue | TupleGet _ | UnionTag _
        | UnionField _ | RecordGet _ -> [e]
        | ExprGet e2 -> [e; e2]
    | Throw(e, _, _) -> [e]
    | Sequential exprs -> exprs
    | Let(bs, body) -> (List.map snd bs) @ [body]
    | IfThenElse(cond, thenExpr, elseExpr) -> [cond; thenExpr; elseExpr]
    | Set(e, kind, v, _) ->
        match kind with
        | VarSet | RecordSet _ -> [e; v]
        | ExprSet e2 -> [e; e2; v]
    | Loop (kind, _) ->
        match kind with
        | While(e1, e2) -> [e1; e2]
        | For(_, e1, e2, e3, _) -> [e1; e2; e3]
    | TryCatch(body, catch, finalizer) ->
        match catch with
        | Some(_,c) -> body::c::(Option.toList finalizer)
        | None -> body::(Option.toList finalizer)
    | DecisionTree(expr, targets) -> expr::(List.map snd targets)
    | DecisionTreeSuccess(_, boundValues, _) -> boundValues

let rec deepExists f expr =
    f expr || (getSubExpressions expr |> List.exists (deepExists f))

let rec tryDeepExists f expr =
    match f expr with
    | Some res -> res
    | None -> getSubExpressions expr |> List.exists (tryDeepExists f)

let replaceValues replacements expr =
    if Map.isEmpty replacements
    then expr
    else expr |> visitFromInsideOut (function
        | IdentExpr id as e ->
            match Map.tryFind id.Name replacements with
            | Some e -> e
            | None -> e
        | e -> e)

// Don't erase expressions referenced 0 times, they may have side-effects
let isReferencedOnlyOnce identName e =
    let limit = 1
    let mutable count = 0
    e |> deepExists (function
        | IdentExpr id2 when id2.Name = identName ->
            count <- count + 1
            count > limit
        | _ -> false) |> ignore
    count = limit

module private Transforms =
    let (|ExprType|) (e: Expr) = e.Type

    let (|EntFullName|_|) fullName (ent: FSharpEntity) =
        match ent.TryFullName with
        | Some fullName2 when fullName = fullName2 -> Some EntFullName
        | _ -> None

    let (|LambdaOrDelegate|_|) = function
        | Function(Lambda arg, body, name) -> Some([arg], body, name)
        | Function(Delegate args, body, name) -> Some(args, body, name)
        | _ -> None

    let rec (|NestedLambda|_|) expr =
        let rec nestedLambda accArgs body name =
            match body with
            | Function(Lambda arg, body, None) ->
                nestedLambda (arg::accArgs) body name
            | _ -> Some(NestedLambda(List.rev accArgs, body, name))
        match expr with
        | Function(Lambda arg, body, name) -> nestedLambda [arg] body name
        | _ -> None

    let (|NestedApply|_|) expr =
        let rec nestedApply r t accArgs applied =
            match applied with
            | Operation(CurriedApply(applied, args), _, _) ->
                nestedApply r t (args@accArgs) applied
            | _ -> Some(NestedApply(applied, accArgs, t, r))
        match expr with
        | Operation(CurriedApply(applied, args), t, r) ->
            nestedApply r t args applied
        | _ -> None

    let (|EvalsMutableIdent|_|) expr =
        if tryDeepExists (function
            | IdentExpr id when id.IsMutable -> Some true
            | Function _ -> Some false // Ignore function bodies
            | _ -> None) expr
        then Some EvalsMutableIdent
        else None

    let lambdaBetaReduction (_: ICompiler) e =
        let applyArgs (args: Ident list) argExprs body =
            let bindings, replacements =
                (([], Map.empty), args, argExprs)
                |||> List.fold2 (fun (bindings, replacements) ident expr ->
                    if hasDoubleEvalRisk expr && not(isReferencedOnlyOnce ident.Name body)
                    then (ident, expr)::bindings, replacements
                    else bindings, Map.add ident.Name expr replacements)
            match bindings with
            | [] -> replaceValues replacements body
            | bindings -> Let(List.rev bindings, replaceValues replacements body)
        match e with
        // TODO: `Invoke` calls for delegates? Partial applications too?
        // TODO: Don't inline if one of the arguments is `this`?
        | Operation(CurriedApply(NestedLambda(args, body, None), argExprs), _, _)
            when List.sameLength args argExprs ->
            applyArgs args argExprs body
        | e -> e

    // TODO: Other erasable getters? (List.head, List.item...)
    let getterBetaReduction (_: ICompiler) = function
        | Get(Value(NewTuple exprs), TupleGet index, _, _) -> List.item index exprs
        | Get(Value(NewOption(Some expr, _)), OptionValue, _, _) -> expr
        | e -> e

    let bindingBetaReduction (_: ICompiler) e =
        match e with
        // Don't try to optimize bindings with multiple ident-value pairs
        // as they can reference each other
        | Let([ident, value], body) when not ident.IsMutable ->
            let identName = ident.Name
            let replacement =
                match value with
                // Don't erase bindings if mutable variables are involved
                // as the value can change in between statements
                | EvalsMutableIdent -> None
                // When replacing an ident with an erased option use the name but keep the unwrapped type
                | Get(IdentExpr id, OptionValue, t, _)
                        when mustWrapOption t |> not ->
                    makeTypedIdent t id.Name |> IdentExpr |> Some
                // TODO: If the ident is referenced 0 times we may delete it if there are no risk
                // of side-effects. (e.g. when assigning JS `this` to first Arg in interface members)
                | value when ident.IsCompilerGenerated
                    // TODO: Can compiler generated values be referenced more than once?
                    && (not(hasDoubleEvalRisk value) || isReferencedOnlyOnce identName body) ->
                    match value with
                    // TODO: Check if current name is Some? Shouldn't happen...
                    | Function(args, body, _) -> Function(args, body, Some identName) |> Some
                    | value -> Some value
                | _ -> None
            match replacement with
            | Some value -> replaceValues (Map [identName, value]) body
            | None -> e
        | e -> e

    let uncurryLambdaType t =
        let rec uncurryLambdaTypeInner acc = function
            | FunctionType(LambdaType argType, returnType) ->
                uncurryLambdaTypeInner (argType::acc) returnType
            | returnType -> List.rev acc, returnType
        match t with
        | FunctionType(LambdaType argType, returnType)
        | Option(FunctionType(LambdaType argType, returnType)) ->
            uncurryLambdaTypeInner [argType] returnType
        | returnType -> [], returnType

    let getUncurriedArity (e: Expr) =
        match e with
        | ExprType(FunctionType(DelegateType argTypes, _))
        | ExprType(Option(FunctionType(DelegateType argTypes, _))) ->
            List.length argTypes |> Some
        | _ -> None

    let uncurryExpr argsAndRetTypes expr =
        let rec (|UncurriedLambda|_|) (argsAndRetTypes, expr) =
            let rec uncurryLambda name accArgs remainingArity expr =
                if remainingArity = Some 0
                then Function(Delegate(List.rev accArgs), expr, name) |> Some
                else
                    match expr, remainingArity with
                    | Function(Lambda arg, body, name2), _ ->
                        let remainingArity = remainingArity |> Option.map (fun x -> x - 1)
                        uncurryLambda (Option.orElse name2 name) (arg::accArgs) remainingArity body
                    // If there's no arity expectation we can return the flattened part
                    | _, None when List.isEmpty accArgs |> not ->
                        Function(Delegate(List.rev accArgs), expr, name) |> Some
                    // We cannot flatten lambda to the expected arity
                    | _, _ -> None
            let arity = argsAndRetTypes |> Option.map (fun (args,_) -> List.length args)
            match expr with
            // Uncurry also function options
            | Value(NewOption(Some expr, _)) ->
                uncurryLambda None [] arity expr
                |> Option.map (fun expr -> Value(NewOption(Some expr, expr.Type)))
            | _ -> uncurryLambda None [] arity expr
        match argsAndRetTypes, expr with
        // | Some (0|1), expr -> expr // This shouldn't happen
        | UncurriedLambda lambda -> lambda
        | Some(argTypes, retType), _ ->
            let arity = List.length argTypes
            match getUncurriedArity expr with
            | Some arity2 when arity = arity2 -> expr
            | _ -> let t = FunctionType(DelegateType argTypes, retType)
                   Replacements.uncurryExpr t arity expr
        | None, _ -> expr

    // TODO!!!: Some cases of coertion shouldn't be erased
    // string :> seq #1279
    // list (and others) :> seq in Fable 2.0
    // concrete type :> interface in Fable 2.0
    let resolveCasts_required (com: ICompiler) = function
        | Cast(e, t) ->
            match t with
            | Pojo caseRule ->
                Replacements.makePojo caseRule e
            | DeclaredType(EntFullName Types.enumerable, _) ->
                Replacements.toSeq com t e
            | DeclaredType(ent, _) when ent.IsInterface ->
                FSharp2Fable.Util.castToInterface com t ent e
            | FunctionType(DelegateType argTypes, returnType) ->
                uncurryExpr (Some(argTypes, returnType)) e
            | _ -> e
        | e -> e

    let uncurryBodies idents body1 body2 =
        let replaceIdentType replacements (id: Ident) =
            match Map.tryFind id.Name replacements with
            | Some(Option nestedType as optionType) ->
                // Check if the ident to replace is an option too or not
                match id.Type with
                | Option _ -> { id with Type = optionType }
                | _ ->        { id with Type = nestedType }
            | Some typ -> { id with Type = typ }
            | None -> id
        let replaceBody uncurried body =
            visitFromInsideOut (function
                | IdentExpr id -> replaceIdentType uncurried id |> IdentExpr
                | e -> e) body
        let uncurried =
            (Map.empty, idents) ||> List.fold (fun uncurried id ->
                match uncurryLambdaType id.Type with
                | argTypes, returnType when List.isMultiple argTypes ->
                    let delType = FunctionType(DelegateType argTypes, returnType)
                    // uncurryLambdaType ignores options, so check the original type
                    match id.Type with
                    | Option _ -> Map.add id.Name (Option delType) uncurried
                    | _ -> Map.add id.Name delType uncurried
                | _ -> uncurried)
        if Map.isEmpty uncurried
        then idents, body1, body2
        else
            let idents = idents |> List.map (replaceIdentType uncurried)
            let body1 = replaceBody uncurried body1
            let body2 = Option.map (replaceBody uncurried) body2
            idents, body1, body2

    let rec lambdaMayEscapeScope identName = function
        | Operation(CurriedApply(IdentExpr ident,_),_,_) when ident.Name = identName -> false
        | IdentExpr ident when ident.Name = identName -> true
        | e -> getSubExpressions e |> List.exists (lambdaMayEscapeScope identName)

    let uncurryArgs argTypes args =
        let mapArgs f argTypes args =
            let rec mapArgsInner f acc argTypes args =
                match argTypes, args with
                | head1::tail1, head2::tail2 ->
                    let x = f head1 head2
                    mapArgsInner f (x::acc) tail1 tail2
                | [], args2 -> (List.rev acc)@args2
                | _, [] -> List.rev acc
            mapArgsInner f [] argTypes args
        match argTypes with
        | Some [] -> args // Do nothing
        | Some argTypes ->
            (argTypes, args) ||> mapArgs (fun argType arg ->
                match uncurryLambdaType argType with
                | argTypes, retType when List.isMultiple argTypes ->
                    uncurryExpr (Some(argTypes, retType)) arg
                | _ -> arg)
        | None -> List.map (uncurryExpr None) args

    let uncurryInnerFunctions (_: ICompiler) = function
        | Let([ident, NestedLambda(args, fnBody, _)], letBody) as e when List.isMultiple args ->
            // We need to check the function doesn't leave the current context
            if lambdaMayEscapeScope ident.Name letBody |> not then
                let idents, fnBody, letBody = uncurryBodies [ident] fnBody (Some letBody)
                Let([List.head idents, Function(Delegate args, fnBody, None)], letBody.Value)
            else e
        // Anonymous lambda immediately applied
        | Operation(CurriedApply((NestedLambda(args, fnBody, Some name) as lambda), argExprs), t, r)
                        when List.isMultiple args && List.sameLength args argExprs ->
            let ident = makeTypedIdent lambda.Type name
            let idents, fnBody, _ = uncurryBodies [ident] fnBody None
            let info = argInfo None argExprs (args |> List.map (fun a -> a.Type) |> Some)
            Function(Delegate args, fnBody, Some (List.head idents).Name)
            |> staticCall r t info
        | e -> e

    let uncurryReceivedArgs_required (_: ICompiler) e =
        match e with
        | Function(Lambda arg, body, name) ->
            let args, body, _ = uncurryBodies [arg] body None
            Function(Lambda (List.head args), body, name)
        | Function(Delegate args, body, name) ->
            let args, body, _ = uncurryBodies args body None
            Function(Delegate args, body, name)
        | e -> e

    let uncurryRecordFields_required (_: ICompiler) = function
        | Value(NewRecord(args, ent, genArgs)) ->
            let args = uncurryArgs None args
            Value(NewRecord(args, ent, genArgs))
        | Get(e, RecordGet(fi, ent), t, r) ->
            let uncurriedType =
                match t with
                | FunctionType(LambdaType _, _) ->
                    let argTypes, retType = uncurryLambdaType t
                    FunctionType(DelegateType argTypes, retType)
                | Option(FunctionType(LambdaType _, _) as t) ->
                    let argTypes, retType = uncurryLambdaType t
                    Option(FunctionType(DelegateType argTypes, retType))
                | _ -> t
            Get(e, RecordGet(fi, ent), uncurriedType, r)
        // If a record function is assigned to a value, just curry it to prevent issues
        | Let(identsAndValues, body) ->
            let identsAndValues =
                identsAndValues |> List.map (fun (ident, value) ->
                    match ident.Type, value with
                    | FunctionType(LambdaType _, _) as t, Get(_, RecordGet _, FunctionType(DelegateType delArgTypes, _), _)
                        when List.isMultiple delArgTypes ->
                            ident, Replacements.curryExpr t (List.length delArgTypes) value
                    | _ -> ident, value)
            Let(identsAndValues, body)
        | e -> e

    let uncurrySendingArgs_required (_: ICompiler) = function
        | Operation(Call(kind, info), t, r) ->
            let info = { info with Args = uncurryArgs info.SignatureArgTypes info.Args }
            Operation(Call(kind, info), t, r)
        | Operation(CurriedApply(callee, args), t, r) ->
            let argTypes = uncurryLambdaType callee.Type |> fst |> Some
            Operation(CurriedApply(callee, uncurryArgs argTypes args), t, r)
        | Operation(Emit(macro, Some info), t, r) ->
            let info = { info with Args = uncurryArgs info.SignatureArgTypes info.Args }
            Operation(Emit(macro, Some info), t, r)
        | e -> e

    let rec uncurryApplications_required e =
        match e with
        | NestedApply(applied, args, t, r) ->
            let applied = visitFromOutsideIn uncurryApplications_required applied
            let args = args |> List.map (visitFromOutsideIn uncurryApplications_required)
            match applied.Type with
            | FunctionType(DelegateType argTypes, _) ->
                if List.sameLength argTypes args then
                    let info = argInfo None args (Some argTypes)
                    staticCall r t info applied |> Some
                else
                    Replacements.partialApply t (argTypes.Length - args.Length) applied args |> Some
            | _ -> Operation(CurriedApply(applied, args), t, r) |> Some
        | _ -> None

    let unwrapFunctions_doNotTraverse (_: ICompiler) e =
        let sameArgs args1 args2 =
            List.sameLength args1 args2
            && List.forall2 (fun (a1: Ident) -> function
                | IdentExpr a2 -> a1.Name = a2.Name
                | _ -> false) args1 args2
        let unwrapFunctionsInner = function
            // TODO: When Option.isSome info.ThisArg we could bind it (also for InstanceCall)
            | LambdaOrDelegate(args, Operation(Call(StaticCall funcExpr, info), _, _), _)
                when Option.isNone info.ThisArg && sameArgs args info.Args -> funcExpr
            | e -> e
        match e with
        // We cannot apply the unwrap optimization to the outmost function,
        // as we would be losing the ValueDeclarationInfo
        | Function(kind, body, name) -> Function(kind, visitFromInsideOut unwrapFunctionsInner body, name)
        | e -> visitFromInsideOut unwrapFunctionsInner e

open Transforms

// ATTENTION: Order of transforms matters for optimizations
// TODO: Optimize binary operations with numerical or string literals
let optimizations =
    [ // First apply beta reduction
      fun com e -> visitFromInsideOut (bindingBetaReduction com) e
      fun com e -> visitFromInsideOut (lambdaBetaReduction com) e
      fun com e -> visitFromInsideOut (getterBetaReduction com) e
      // Then resolve casts
      fun com e -> visitFromInsideOut (resolveCasts_required com) e
      // Then apply uncurry optimizations
      // Required as fable-core and bindings expect it
      fun com e -> visitFromInsideOut (uncurryReceivedArgs_required com) e
      fun com e -> visitFromInsideOut (uncurryRecordFields_required com) e
      fun com e -> visitFromInsideOut (uncurrySendingArgs_required com) e
      fun com e -> visitFromInsideOut (uncurryInnerFunctions com) e
      fun _   e -> visitFromOutsideIn uncurryApplications_required e
      unwrapFunctions_doNotTraverse
    ]

let optimizeExpr (com: ICompiler) e =
    List.fold (fun e f -> f com e) e optimizations

let rec optimizeDeclaration (com: ICompiler) = function
    | ActionDeclaration expr ->
        ActionDeclaration(optimizeExpr com expr)
    | ValueDeclaration(value, info) ->
        ValueDeclaration(optimizeExpr com value, info)
    | ImplicitConstructorDeclaration(args, body, info) ->
        ImplicitConstructorDeclaration(args, optimizeExpr com body, info)
    | OverrideDeclaration(args, body, info) ->
        OverrideDeclaration(args, optimizeExpr com body, info)
    | InterfaceCastDeclaration(members, info) ->
        let members = members |> List.map (fun (n,v,k) -> n, optimizeExpr com v, k)
        InterfaceCastDeclaration(members, info)

let optimizeFile (com: ICompiler) (file: File) =
    let newDecls = List.map (optimizeDeclaration com) file.Declarations
    File(file.SourcePath, newDecls, usedVarNames=file.UsedVarNames, dependencies=file.Dependencies)