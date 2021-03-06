﻿[<AutoOpen>]
module FreyaMusicStore.Common

open System

open Freya.Core

module Async =
    let map f x = async { let! v = x in return f v }

module Option =
    let fromNullable = function | null -> None | x -> Some x

module Either =
    let Success = Choice1Of2
    let Failure = Choice2Of2

    let (|Success|Failure|) m =
        match m with
        | Choice1Of2 x -> Success x
        | Choice2Of2 x -> Failure x

    let bind f m =
        match m with
        | Success x -> f x
        | Failure err -> Failure err

    let toOption =
        function Success x -> Some x | _ -> None

module Tuple =
    let map f (x,y) = f x, f y

[<AutoOpen>]
module Utils =
    
    let passHash (pass: string) =
        use sha = Security.Cryptography.SHA256.Create()
        Text.Encoding.UTF8.GetBytes(pass)
        |> sha.ComputeHash
        |> Array.map (fun b -> b.ToString("x2"))
        |> String.concat ""

    type MaybeBuilder() =
        member __.Bind(m, f) = Option.bind f m
        member __.Return(x) = Some x
        member __.ReturnFrom(x) = x

    let maybe = MaybeBuilder()

    type Auth = {
        UserName : string
        Role : string
    }

[<AutoOpen>]
module Katana = 
    open System.Security.Claims

    open Microsoft.AspNet.Identity
    open Microsoft.Owin
    open Microsoft.Owin.Security

    let getEnv: Freya<FreyaEnvironment> = (fun freyaState -> async { return freyaState.Environment, freyaState })

    let toAuth (authResult: AuthenticateResult) =
        maybe {
            let! nameClaim = authResult.Identity.Claims |> Seq.tryFind (fun claim -> claim.Type = ClaimTypes.Name)
            let! roleClaim = authResult.Identity.Claims |> Seq.tryFind (fun claim -> claim.Type = ClaimTypes.Role)
            return {
                UserName = nameClaim.Value
                Role = roleClaim.Value
            }
        }

    let authType = DefaultAuthenticationTypes.ApplicationCookie

    let auth (ctx: OwinContext) =
        ctx.Authentication.AuthenticateAsync(authType) 
        |> Async.AwaitTask 
        |> Async.map (Option.fromNullable >> Option.bind toAuth)

    let ctxSignIn auth (ctx: OwinContext) =
        let claims = 
                [ Claim(ClaimTypes.Role, auth.Role)
                  Claim(ClaimTypes.Name, auth.UserName) ]
        ctx.Authentication.SignIn(ClaimsIdentity(claims, authType))

    let ctxSignOut (ctx: OwinContext) =
        ctx.Authentication.SignOut(authType)

    let owinContext = getEnv |> Freya.map (fun env -> OwinContext(env))

    let setResponseCookie key value = owinContext |> Freya.map (fun ctx -> ctx.Response.Cookies.Append(key, value))

    let getRequestCookie key = owinContext |> Freya.map (fun ctx -> ctx.Request.Cookies.[key] |> Option.fromNullable)

    let deleteResponseCookie key = owinContext |> Freya.map (fun ctx -> ctx.Response.Cookies.Delete(key, CookieOptions()))

    let getAuth = owinContext |> Freya.bind (Freya.fromAsync auth) |> Freya.memo
    
    let signIn auth = owinContext |> Freya.map (ctxSignIn auth)
    
    let signOut = owinContext |> Freya.map ctxSignOut

    let isAuthenticated = getAuth |> Freya.map Option.isSome

    let isAdmin = getAuth |> Freya.map (Option.exists (fun auth -> auth.Role = "admin"))


[<AutoOpen>]
module Parsing =
    open System.IO
    open System.Globalization
    
    open Arachne.Http

    open Chiron
    open Chiron.Operators

    open Freya.Lenses.Http

    let mInt (s: string) = 
        match Int32.TryParse s with
        | true, x -> Some x
        | _ -> None

    let mDec (s: string) = 
        match Decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) with
        | true, x -> Some x
        | _ -> None

    let keyValue (s: string) =
        match s.Split([| '=' |]) with
        | [|k;v|] -> Some(k,v)
        | _ -> None

    let decode = System.Net.WebUtility.UrlDecode

    let toMap (s: string) =
        s.Split([| '&' |]) 
        |> Array.choose keyValue 
        |> Array.map (Tuple.map decode) 
        |> Map.ofArray

    let readStream (x: Stream) =
        use reader = new StreamReader (x)
        reader.ReadToEndAsync()
        |> Async.AwaitTask

    let query = 
        Freya.getLens Request.query 
        |> Freya.map toMap 
        |> Freya.memo

    let body =
        Freya.getLens Request.body 
        |> Freya.bind (Freya.fromAsync readStream) 
    
    let form = 
        body
        |> Freya.map toMap 
        |> Freya.memo

    type AlbumForm =
        { Title : string
          ArtistId : int
          GenreId : int
          Price : decimal
          AlbumArtUrl : string }

        static member FromJson (_: AlbumForm) =
                fun t a g p art ->
                    { Title = t
                      ArtistId = a
                      GenreId = g
                      Price = p
                      AlbumArtUrl  = art }
            <!> Json.read "title"
            <*> Json.read "artistId"
            <*> Json.read "genreId"
            <*> Json.read "price"
            <*> Json.read "albumArtUrl"

    let readAlbum =
        freya {
            let! contentType = Freya.getLensPartial Request.Headers.contentType
            match contentType with
            | Some (ContentType (MediaType (Type "application", SubType "x-www-form-urlencoded",_))) -> 
                let! form = form
                let album =
                    maybe {
                        let! title = form |> Map.tryFind "Title"
                        let! artistId = form |> Map.tryFind "ArtistId" |> Option.bind mInt
                        let! genreId = form |> Map.tryFind "GenreId" |> Option.bind mInt
                        let! price = form |> Map.tryFind "Price" |> Option.bind mDec
                        let! albumArtUrl = form |> Map.tryFind "ArtUrl"
                        return 
                            { Title = title
                              ArtistId = artistId
                              GenreId = genreId
                              Price = price
                              AlbumArtUrl = albumArtUrl }
                    }
                return album
            | Some (ContentType m) when m = MediaType.Json ->
                let! body = body
                return (Json.tryParse body |> Either.bind Json.tryDeserialize |> Either.toOption)
            | _ ->
                return None
        } |> Freya.memo

[<AutoOpen>]
module Representations =
    open System.Text

    open Arachne.Http
    
    open Chiron
    
    open Freya.Machine.Extensions.Http

    open RazorEngine.Templating

    let inline writeHtml (view : string, model : 'a) =
        freya {
            let! authResult = getAuth
            let! cartId = getRequestCookie "cartId"
            let albumsInCart cartId = Db.getCartsDetails cartId (Db.getContext()) |> List.sumBy (fun c -> c.Count)
            let viewBag = DynamicViewBag()
            match authResult, cartId with
            | Some authResult, _ ->
                viewBag.AddValue("CartItems", albumsInCart authResult.UserName)
                viewBag.AddValue("UserName", authResult.UserName)
            | _, Some cartId ->
                viewBag.AddValue("CartItems", albumsInCart cartId)
                viewBag.AddValue("CartId", cartId)
            | _ ->
                ()

            let result = RazorEngine.Engine.Razor.RunCompile(view, typeof<'a>, model, viewBag)
            return {
                Data = Encoding.UTF8.GetBytes result
                Description =
                    { Charset = Some Charset.Utf8
                      Encodings = None
                      MediaType = Some MediaType.Html
                      Languages = None } } }

    let inline repJson x =
        Freya.init
            { Data = (Json.serialize >> Json.format >> Encoding.UTF8.GetBytes) x
              Description =
                { Charset = Some Charset.Utf8
                  Encodings = None
                  MediaType = Some MediaType.Json
                  Languages = None } }
    
    let inline ok fetch name spec =
        freya {
            let ctx = Db.getContext()
            let! fetch = fetch
            let res = fetch ctx
            return!
                match spec.MediaTypes with
                | Free -> repJson res
                | Negotiated (m :: _) when m = MediaType.Json -> repJson res
                | Negotiated (m :: _) when m = MediaType.Html -> writeHtml (name, res)
                | _ -> failwith "Representation Failure"
        }

    module Logon = 
        type Logon = {
            ReturnUrl : string
            ValidationMsg : string
        }

    let onUnauthorized returnUrl _ =
        freya {
            let! returnUrl = returnUrl
            return! writeHtml ("logon", {Logon.Logon.ReturnUrl = returnUrl; Logon.Logon.ValidationMsg = ""})
        }

    let onForbidden _ =
        freya {
            return! writeHtml ("forbidden", ())
        }

[<AutoOpen>]
module Machine = 
    open Arachne.Http

    open Freya.Machine
    open Freya.Machine.Extensions.Http
    open Freya.Lenses.Http

    let common =
        freyaMachine {
            using http
            mediaTypesSupported (Freya.init [MediaType.Html]) }

    let inline res fetch name =
        freyaMachine {
            using http
            mediaTypesSupported (Freya.init [MediaType.Html; MediaType.Json])
            handleOk (ok fetch name) } 

    let onMeths meths f def = 
        freya {
            let! meth = Freya.getLens Request.meth
            if meths |> List.exists ((=) meth) then
                return! f
            else 
                return def
        }

    let protectAuthenticated meths returnUrl =
        freyaMachine {
            authorized (onMeths meths isAuthenticated true)
            handleUnauthorized (onUnauthorized returnUrl)
        }

    let protectAdmin meths =
        freyaMachine {
            allowed (onMeths meths isAdmin true)
            handleForbidden onForbidden
        }