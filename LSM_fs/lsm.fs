﻿(*
    Copyright 2014 Zumero, LLC

    Licensed under the Apache License, Version 2.0 (the "License");
    you may not use this file except in compliance with the License.
    You may obtain a copy of the License at

        http://www.apache.org/licenses/LICENSE-2.0

    Unless required by applicable law or agreed to in writing, software
    distributed under the License is distributed on an "AS IS" BASIS,
    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
    See the License for the specific language governing permissions and
    limitations under the License.
*)

namespace Zumero.LSM.fs

open System
open System.IO
open System.Collections.Generic

open Zumero.LSM

module utils =
    // TODO why aren't the functions here in utils using curried params?

    let SeekPage(strm:Stream, pageSize, pageNumber) =
        if 0 = pageNumber then raise (new Exception())
        let pos = ((int64 pageNumber) - 1L) * int64 pageSize
        let newpos = strm.Seek(pos, SeekOrigin.Begin)
        if pos <> newpos then raise (new Exception())

    let ReadFully(strm:Stream, buf, off, len) =
        let mutable sofar = 0
        while sofar<len do
            let got = strm.Read(buf, off + sofar, len - sofar)
            if 0 = got then raise (new Exception())
            sofar <- sofar + got

    let ReadAll (strm:Stream) =
        // TODO this code seems to assume s.Position is 0
        let len = int strm.Length
        let buf:byte[] = Array.zeroCreate len
        let mutable sofar = 0
        while sofar<len do
            let got = strm.Read(buf, sofar, len - sofar)
            //if 0 = got then throw?
            sofar <- sofar + got
        buf

(*
type SeekOp = SEEK_EQ=0 | SEEK_LE=1 | SEEK_GE=2

type ICursor =
    abstract member Seek : k:byte[] * sop:SeekOp -> unit
    abstract member First : unit -> unit
    abstract member Last : unit -> unit
    abstract member Next : unit -> unit
    abstract member Prev : unit -> unit
    abstract member IsValid : unit -> bool
    abstract member Key : unit -> byte[]
    abstract member Value : unit -> Stream
    abstract member ValueLength : unit -> int
    abstract member KeyCompare : k:byte[] -> int

type IWrite = 
    abstract member Insert: k:byte[] * s:Stream -> unit
    abstract member Delete: k:byte[] -> unit
    abstract member OpenCursor: unit -> ICursor
*)

module ByteComparer = 
    // this code is very non-F#-ish.  but it's much faster than the
    // idiomatic version which precedded it.

    let Compare (x:byte[]) (y:byte[]) =
        let xlen = x.Length
        let ylen = y.Length
        let len = if xlen<ylen then xlen else ylen
        let mutable i = 0
        let mutable result = 0
        while i<len do
            let c = (int (x.[i])) - int (y.[i])
            if c <> 0 then
                i <- len+1 // breaks out of the loop, and signals that result is valid
                result <- c
            else
                i <- i + 1
        if i>len then result else (xlen - ylen)

    let CompareWithin (x:byte[]) off xlen (y:byte[]) =
        let ylen = y.Length
        let len = if xlen<ylen then xlen else ylen
        let mutable i = 0
        let mutable result = 0
        while i<len do
            let c = (int (x.[i + off])) - int (y.[i])
            if c <> 0 then
                i <- len+1 // breaks out of the loop, and signals that result is valid
                result <- c
            else
                i <- i + 1
        if i>len then result else (xlen - ylen)

type private PageBuilder(pgsz:int) =
    let mutable cur = 0
    let buf:byte[] = Array.zeroCreate pgsz

    member this.Reset() = cur <- 0
    member this.Flush(s:Stream) = s.Write(buf, 0, buf.Length)
    member this.PageSize = buf.Length
    member this.Position = cur
    member this.Available = buf.Length - cur
    member this.SetPageFlag(x:byte) = buf.[1] <- buf.[1] ||| x

    member this.PutByte(x:byte) =
        buf.[cur] <- byte x
        cur <- cur+1

    member this.PutStream(s:Stream, len:int) =
        utils.ReadFully(s, buf, cur, len)
        cur <- cur+len

    member this.PutArray(ba:byte[]) =
        System.Array.Copy (ba, 0, buf, cur, ba.Length)
        cur <- cur + ba.Length

    member this.PutInt32(ov:int) =
        // assert ov >= 0
        let v:uint32 = uint32 ov
        buf.[cur+0] <- byte (v >>>  24)
        buf.[cur+1] <- byte (v >>>  16)
        buf.[cur+2] <- byte (v >>>  8)
        buf.[cur+3] <- byte (v >>>  0)
        cur <- cur+4

    member this.PutInt32At(at:int, ov:int) =
        // assert ov >= 0
        let v:uint32 = uint32 ov
        buf.[at+0] <- byte (v >>>  24)
        buf.[at+1] <- byte (v >>>  16)
        buf.[at+2] <- byte (v >>>  8)
        buf.[at+3] <- byte (v >>>  0)

    member this.SetBoundaryNextPageField(page:int) = this.PutInt32At(buf.Length - 4, page)

    member this.PutInt16(ov:int) =
        // assert ov >= 0
        let v:uint32 = uint32 ov
        buf.[cur+0] <- byte (v >>>  8)
        buf.[cur+1] <- byte (v >>>  0)
        cur <- cur+2

    member this.PutInt16At(at:int, ov:int) =
        // assert ov >= 0
        let v:uint32 = uint32 ov
        buf.[at+0] <- byte (v >>>  8)
        buf.[at+1] <- byte (v >>>  0)

    member this.PutVarint(ov:int64) =
        // assert ov >= 0
        let v:uint64 = uint64 ov
        if v<=240UL then 
            buf.[cur] <- byte v
            cur <- cur + 1
        else if v<=2287UL then 
            buf.[cur] <- byte ((v - 240UL) / 256UL + 241UL)
            buf.[cur+1] <- byte ((v - 240UL) % 256UL)
            cur <- cur + 2
        else if v<=67823UL then 
            buf.[cur] <- 249uy
            buf.[cur+1] <- byte ((v - 2288UL) / 256UL)
            buf.[cur+2] <- byte ((v - 2288UL) % 256UL)
            cur <- cur + 3
        else if v<=16777215UL then 
            buf.[cur] <- 250uy
            buf.[cur+1] <- byte (v >>> 16)
            buf.[cur+2] <- byte (v >>>  8)
            buf.[cur+3] <- byte (v >>>  0)
            cur <- cur + 4
        else if v<=4294967295UL then 
            buf.[cur] <- 251uy
            buf.[cur+1] <- byte (v >>> 24)
            buf.[cur+2] <- byte (v >>> 16)
            buf.[cur+3] <- byte (v >>>  8)
            buf.[cur+4] <- byte (v >>>  0)
            cur <- cur + 5
        else if v<=1099511627775UL then 
            buf.[cur] <- 252uy
            buf.[cur+1] <- byte (v >>> 32)
            buf.[cur+2] <- byte (v >>> 24)
            buf.[cur+3] <- byte (v >>> 16)
            buf.[cur+4] <- byte (v >>>  8)
            buf.[cur+5] <- byte (v >>>  0)
            cur <- cur + 6
        else if v<=281474976710655UL then 
            buf.[cur] <- 253uy
            buf.[cur+1] <- byte (v >>> 40)
            buf.[cur+2] <- byte (v >>> 32)
            buf.[cur+3] <- byte (v >>> 24)
            buf.[cur+4] <- byte (v >>> 16)
            buf.[cur+5] <- byte (v >>>  8)
            buf.[cur+6] <- byte (v >>>  0)
            cur <- cur + 7
        else if v<=72057594037927935UL then 
            buf.[cur] <- 254uy
            buf.[cur+1] <- byte (v >>> 48)
            buf.[cur+2] <- byte (v >>> 40)
            buf.[cur+3] <- byte (v >>> 32)
            buf.[cur+4] <- byte (v >>> 24)
            buf.[cur+5] <- byte (v >>> 16)
            buf.[cur+6] <- byte (v >>>  8)
            buf.[cur+7] <- byte (v >>>  0)
            cur <- cur + 8
        else
            buf.[cur] <- 255uy
            buf.[cur+1] <- byte (v >>> 56)
            buf.[cur+2] <- byte (v >>> 48)
            buf.[cur+3] <- byte (v >>> 40)
            buf.[cur+4] <- byte (v >>> 32)
            buf.[cur+5] <- byte (v >>> 24)
            buf.[cur+6] <- byte (v >>> 16)
            buf.[cur+7] <- byte (v >>>  8)
            buf.[cur+8] <- byte (v >>>  0)
            cur <- cur + 9

type private PageReader(pgsz:int) =
    let mutable cur = 0
    let buf:byte[] = Array.zeroCreate pgsz

    member this.Position = cur
    member this.PageSize = buf.Length
    member this.SetPosition(x) = cur <- x
    member this.Read(s:Stream) = utils.ReadFully(s, buf, 0, buf.Length)
    member this.Reset() = cur <- 0
    member this.Compare(len, other) = ByteComparer.CompareWithin buf cur len other
    member this.PageType = buf.[0]
    member this.Skip(len) = cur <- cur + len

    member this.GetByte() =
        let r = buf.[cur]
        cur <- cur + 1
        r

    member this.GetInt32() :int =
        let a0 = uint64 buf.[cur+0]
        let a1 = uint64 buf.[cur+1]
        let a2 = uint64 buf.[cur+2]
        let a3 = uint64 buf.[cur+3]
        let r = (a0 <<< 24) ||| (a1 <<< 16) ||| (a2 <<< 8) ||| (a3 <<< 0)
        cur <- cur + 4
        // assert r fits in a 32 bit signed int
        int r

    member this.GetInt32At(at) :int =
        let a0 = uint64 buf.[at+0]
        let a1 = uint64 buf.[at+1]
        let a2 = uint64 buf.[at+2]
        let a3 = uint64 buf.[at+3]
        let r = (a0 <<< 24) ||| (a1 <<< 16) ||| (a2 <<< 8) ||| (a3 <<< 0)
        // assert r fits in a 32 bit signed int
        int r

    member this.CheckPageFlag(f) = 0uy <> (buf.[1] &&& f)
    member this.GetBoundaryNextPageField() = this.GetInt32At(buf.Length - 4)

    member this.GetArray(len) =
        let ba:byte[] = Array.zeroCreate len
        System.Array.Copy(buf, cur, ba, 0, len)
        cur <- cur + len
        ba

    member this.GetInt16() :int =
        let a0 = uint64 buf.[cur+0]
        let a1 = uint64 buf.[cur+1]
        let r = (a0 <<< 8) ||| (a1 <<< 0)
        cur <- cur + 2
        // assert r fits in a 16 bit signed int
        let r2 = int16 r
        int r2

    member this.GetVarint() :int64 =
        let a0 = uint64 buf.[cur]
        if a0 <= 240UL then 
            cur <- cur + 1
            int64 a0
        else if a0 <= 248UL then
            let a1 = uint64 buf.[cur+1]
            cur <- cur + 2
            let r = (240UL + 256UL * (a0 - 241UL) + a1)
            int64 r
        else if a0 = 249UL then
            let a1 = uint64 buf.[cur+1]
            let a2 = uint64 buf.[cur+2]
            cur <- cur + 3
            let r = (2288UL + 256UL * a1 + a2)
            int64 r
        else if a0 = 250UL then
            let a1 = uint64 buf.[cur+1]
            let a2 = uint64 buf.[cur+2]
            let a3 = uint64 buf.[cur+3]
            cur <- cur + 4
            let r = (a1<<<16) ||| (a2<<<8) ||| a3
            int64 r
        else if a0 = 251UL then
            let a1 = uint64 buf.[cur+1]
            let a2 = uint64 buf.[cur+2]
            let a3 = uint64 buf.[cur+3]
            let a4 = uint64 buf.[cur+4]
            cur <- cur + 5
            let r = (a1<<<24) ||| (a2<<<16) ||| (a3<<<8) ||| a4
            int64 r
        else if a0 = 252UL then
            let a1 = uint64 buf.[cur+1]
            let a2 = uint64 buf.[cur+2]
            let a3 = uint64 buf.[cur+3]
            let a4 = uint64 buf.[cur+4]
            let a5 = uint64 buf.[cur+5]
            cur <- cur + 6
            let r = (a1<<<32) ||| (a2<<<24) ||| (a3<<<16) ||| (a4<<<8) ||| a5
            int64 r
        else if a0 = 253UL then
            let a1 = uint64 buf.[cur+1]
            let a2 = uint64 buf.[cur+2]
            let a3 = uint64 buf.[cur+3]
            let a4 = uint64 buf.[cur+4]
            let a5 = uint64 buf.[cur+5]
            let a6 = uint64 buf.[cur+6]
            cur <- cur + 7
            let r = (a1<<<40) ||| (a2<<<32) ||| (a3<<<24) ||| (a4<<<16) ||| (a5<<<8) ||| a6
            int64 r
        else if a0 = 254UL then
            let a1 = uint64 buf.[cur+1]
            let a2 = uint64 buf.[cur+2]
            let a3 = uint64 buf.[cur+3]
            let a4 = uint64 buf.[cur+4]
            let a5 = uint64 buf.[cur+5]
            let a6 = uint64 buf.[cur+6]
            let a7 = uint64 buf.[cur+7]
            cur <- cur + 8
            let r = (a1<<<48) ||| (a2<<<40) ||| (a3<<<32) ||| (a4<<<24) ||| (a5<<<16) ||| (a6<<<8) ||| a7
            int64 r
        else
            let a1 = uint64 buf.[cur+1]
            let a2 = uint64 buf.[cur+2]
            let a3 = uint64 buf.[cur+3]
            let a4 = uint64 buf.[cur+4]
            let a5 = uint64 buf.[cur+5]
            let a6 = uint64 buf.[cur+6]
            let a7 = uint64 buf.[cur+7]
            let a8 = uint64 buf.[cur+8]
            cur <- cur + 9
            let r = (a1<<<56) ||| (a2<<<48) ||| (a3<<<40) ||| (a4<<<32) ||| (a5<<<24) ||| (a6<<<16) ||| (a7<<<8) ||| a8
            int64 r

type MemorySegment() =
    let pairs = new System.Collections.Generic.Dictionary<byte[],Stream>()

     // Here in the F# version, the cursor is implemented as an object
     // expression instead of a private inner class, which F# does
     // not support.  Actually, I prefer this because the object
     // expression can access private class instance fields directly
     // whereas the nested class in C# cannot.

    let openCursor() = 
        // The following is a ref cell because mutable variables
        // cannot be captured by a closure.
        let cur = ref -1
        let keys:byte[][] = (Array.ofSeq pairs.Keys)
        let sortfunc x y = ByteComparer.Compare x y
        Array.sortInPlaceWith sortfunc keys

        let rec search k min max sop le ge = 
            if max < min then
                match sop with
                | SeekOp.SEEK_EQ -> -1
                | SeekOp.SEEK_LE -> le
                | _ -> ge
            else
                let mid = (max + min) / 2
                let kmid = keys.[mid]
                let cmp = ByteComparer.Compare kmid k
                if 0 = cmp then mid
                else if cmp<0  then search k (mid+1) max sop mid ge
                else search k min (mid-1) sop le mid

        { new ICursor with
            member this.IsValid() =
                (!cur >= 0) && (!cur < keys.Length)

            member this.First() =
                cur := 0

            member this.Last() =
                cur := keys.Length - 1
            
            member this.Next() =
                cur := !cur + 1

            member this.Prev() =
                cur := !cur - 1

            member this.Key() =
                keys.[!cur]
            
            member this.KeyCompare(k) =
                ByteComparer.Compare (keys.[!cur]) k

            member this.Value() =
                let v = pairs.[keys.[!cur]]
                if v <> null then ignore (v.Seek(0L, SeekOrigin.Begin))
                v

            member this.ValueLength() =
                let v = pairs.[keys.[!cur]]
                if v <> null then (int v.Length) else -1

            member this.Seek (k, sop) =
                cur := search k 0 (keys.Length-1) sop -1 -1
        }

    interface IWrite with
        member this.Insert (k:byte[], s:Stream) =
            pairs.[k] <- s

        member this.Delete (k:byte[]) =
            pairs.[k] <- null

        member this.OpenCursor() =
            openCursor()

    static member Create() :IWrite =
        upcast (new MemorySegment())

type private Direction = FORWARD=0 | BACKWARD=1 | WANDERING=2

type MultiCursor(_subcursors:IEnumerable<ICursor>) =
    let subcursors = List.ofSeq _subcursors
    let mutable cur:ICursor option = None
    let mutable dir = Direction.WANDERING

    let validSorted sortfunc = 
        let valids = List.filter (fun (csr:ICursor) -> csr.IsValid()) subcursors
        let sorted = List.sortWith sortfunc valids
        sorted

    let find sortfunc = 
        let vs = validSorted sortfunc
        if vs.IsEmpty then None
        else Some vs.Head

    let findMin() = 
        let sortfunc (a:ICursor) (b:ICursor) = a.KeyCompare(b.Key())
        find sortfunc
    
    let findMax() = 
        let sortfunc (a:ICursor) (b:ICursor) = b.KeyCompare(a.Key())
        find sortfunc

    static member create([<ParamArray>] _subcursors: ICursor[]) :ICursor =
        upcast (MultiCursor _subcursors)
               
    interface ICursor with
        member this.IsValid() = 
            match cur with
            | Some csr -> csr.IsValid()
            | None -> false

        member this.First() =
            let f (x:ICursor) = x.First()
            List.iter f subcursors
            cur <- findMin()

        member this.Last() =
            let f (x:ICursor) = x.Last()
            List.iter f subcursors
            cur <- findMax()

        // the following members are asking for the value of cur (an option)
        // without checking or matching on it.  they'll crash if cur is None.
        // this matches the C# behavior and the expected behavior of ICursor.
        // don't call these methods without checking IsValid() first.
        member this.Key() = cur.Value.Key()
        member this.KeyCompare(k) = cur.Value.KeyCompare(k)
        member this.Value() = cur.Value.Value()
        member this.ValueLength() = cur.Value.ValueLength()

        member this.Next() =
            let k = cur.Value.Key()
            let f (csr:ICursor) :unit = 
                if (dir <> Direction.FORWARD) && (csr <> cur.Value) then csr.Seek (k, SeekOp.SEEK_GE)
                if csr.IsValid() && (0 = csr.KeyCompare(k)) then csr.Next()
            List.iter f subcursors
            cur <- findMin()
            dir <- Direction.FORWARD

        member this.Prev() =
            let k = cur.Value.Key()
            let f (csr:ICursor) :unit = 
                if (dir <> Direction.BACKWARD) && (csr <> cur.Value) then csr.Seek (k, SeekOp.SEEK_LE)
                if csr.IsValid() && (0 = csr.KeyCompare(k)) then csr.Prev()
            List.iter f subcursors
            cur <- findMax()
            dir <- Direction.BACKWARD

        member this.Seek (k, sop) =
            cur <- None
            dir <- Direction.WANDERING
            let f (csr:ICursor) :bool =
                csr.Seek (k, sop)
                if cur.IsNone && csr.IsValid() && ( (SeekOp.SEEK_EQ = sop) || (0 = csr.KeyCompare (k)) ) then 
                    cur <- Some csr
                    true
                else
                    false
            if not (List.exists f subcursors) then
                match sop with
                | SeekOp.SEEK_GE ->
                    cur <- findMin()
                    if cur.IsSome then dir <- Direction.FORWARD
                | SeekOp.SEEK_LE ->
                    cur <- findMax()
                    if cur.IsSome then dir <- Direction.BACKWARD
                | _ -> ()

type LivingCursor(ch:ICursor) =
    let chain = ch

    let skipTombstonesForward() =
        while (chain.IsValid() && (chain.ValueLength() < 0)) do
            chain.Next()

    let skipTombstonesBackward() =
        while (chain.IsValid() && (chain.ValueLength() < 0)) do
            chain.Prev()

    interface ICursor with
        member this.First() = 
            chain.First()
            skipTombstonesForward()

        member this.Last() = 
            chain.Last()
            skipTombstonesBackward()

        member this.Key() = chain.Key()
        member this.Value() = chain.Value()
        member this.ValueLength() = chain.ValueLength()
        member this.IsValid() = chain.IsValid() && (chain.ValueLength() > 0)
        member this.KeyCompare k = chain.KeyCompare k

        member this.Next() =
            chain.Next()
            skipTombstonesForward()

        member this.Prev() =
            chain.Prev()
            skipTombstonesBackward()
        
        member this.Seek (k, sop) =
            chain.Seek (k, sop)
            match sop with
            | SeekOp.SEEK_GE -> skipTombstonesForward()
            | SeekOp.SEEK_LE -> skipTombstonesBackward()
            | _ -> ()

module Varint =
    let SpaceNeededFor v :int = 
        if v<=240L then 1
        else if v<=2287L then 2
        else if v<=67823L then 3
        else if v<=16777215L then 4
        else if v<=4294967295L then 5
        else if v<=1099511627775L then 6
        else if v<=281474976710655L then 7
        else if v<=72057594037927935L then 8
        else 9

module BTreeSegment =
    let private LEAF_NODE:byte = 1uy
    let private PARENT_NODE:byte = 2uy
    let private OVERFLOW_NODE:byte = 3uy
    // flags on values
    let private FLAG_OVERFLOW:byte = 1uy
    let private FLAG_TOMBSTONE:byte = 2uy
    // flags on pages
    let private FLAG_ROOT_NODE:byte = 1uy
    let private FLAG_BOUNDARY_NODE:byte = 2uy
    let private OVERFLOW_PAGE_HEADER_SIZE = 6
    let private PARENT_NODE_HEADER_SIZE = 8
    let private LEAF_HEADER_SIZE = 8
    let private OFFSET_COUNT_PAIRS = 6

    let private putArrayWithLength (pb:PageBuilder) (ba:byte[]) =
        if null = ba then
            pb.PutByte(FLAG_TOMBSTONE)
            pb.PutVarint(0L)
        else
            pb.PutByte(0uy)
            pb.PutVarint(int64 ba.Length)
            pb.PutArray(ba)

    let private putStreamWithLength (pb:PageBuilder) (ba:Stream) vlen =
        if null = ba then
            pb.PutByte(FLAG_TOMBSTONE)
            pb.PutVarint(0L)
        else
            pb.PutByte(0uy)
            pb.PutVarint(int64 vlen)
            pb.PutStream(ba, int vlen)

    let private writePartialOverflow (pb:PageBuilder) (fs:Stream) (ba:Stream) numPagesToWrite _sofar =
        let len = int ba.Length

        let mutable sofar = _sofar
        let mutable count = 0

        for i in 0 .. numPagesToWrite-1 do
            pb.Reset()
            pb.PutByte(OVERFLOW_NODE)
            pb.PutByte(0uy)
            pb.PutInt32(numPagesToWrite - count)
            // check for the likely partial page at the end
            let num = Math.Min((pb.PageSize - OVERFLOW_PAGE_HEADER_SIZE), (len - sofar))
            pb.PutStream(ba, num)
            sofar <- sofar + num
            pb.Flush(fs)
            count <- count + 1
        sofar

    let private writeOverflowBoundaryPage (pb:PageBuilder) (fs:Stream) (ba:Stream) _sofar nextPageNumber =
        let len = int ba.Length

        let mutable sofar = _sofar

        pb.Reset()
        pb.PutByte(OVERFLOW_NODE)
        pb.PutByte(FLAG_BOUNDARY_NODE)
        pb.PutInt32(0) // TODO really?
        // check for the possible partial page at the end
        let num = Math.Min((pb.PageSize - OVERFLOW_PAGE_HEADER_SIZE - 4), (len - sofar))
        pb.PutStream(ba, num)
        sofar <- sofar + num
        pb.SetBoundaryNextPageField(nextPageNumber)
        pb.Flush(fs)

        sofar

    let private countOverflowPagesFor pageSize len = 
        // this assumes no boundary pages
        let bytesPerPage = pageSize - OVERFLOW_PAGE_HEADER_SIZE
        let pages = len / bytesPerPage
        let extra = if (len % bytesPerPage) <> 0 then 1 else 0
        pages + extra

    let private writeOverflow (pageManager:IPages) startingNextPageNumber startingBoundaryPageNumber (pb:PageBuilder) (fs:Stream) (ba:Stream) =
        let mutable sofar = 0
        let len = (int ba.Length)
        let mutable nextPageNumber = startingNextPageNumber
        let mutable boundaryPageNumber = startingBoundaryPageNumber
        let mutable pagesWritten = 0

        while sofar < len do
            let needed = countOverflowPagesFor (pb.PageSize) (len - sofar)
            let availableBeforeBoundary = if boundaryPageNumber > 0 then (boundaryPageNumber - nextPageNumber) else needed
            let numPages = Math.Min(needed, availableBeforeBoundary)
            sofar <- writePartialOverflow pb fs ba numPages sofar
            pagesWritten <- pagesWritten + numPages
            nextPageNumber <- nextPageNumber + numPages

            if sofar < len then
                // assert nextPageNumber = boundaryPageNumber
                let newRange = pageManager.GetRange()
                nextPageNumber <- fst newRange
                boundaryPageNumber <- snd newRange
                sofar <- writeOverflowBoundaryPage pb fs ba sofar nextPageNumber
                pagesWritten <- pagesWritten + 1
                utils.SeekPage(fs, pb.PageSize, nextPageNumber)

        (nextPageNumber, boundaryPageNumber)

    let private buildParentPage flags firstLeaf lastLeaf (overflows:System.Collections.Generic.Dictionary<int,int32>) (pb:PageBuilder) (children:System.Collections.Generic.List<int32 * byte[]>) stop start =
        // assert stop > start
        let countKeys = stop - start
        pb.Reset ()
        pb.PutByte (PARENT_NODE)
        pb.PutByte (flags)
        pb.PutInt16 (countKeys)
        if 0uy <> (flags &&& FLAG_ROOT_NODE) then
            pb.PutInt32(firstLeaf)
            pb.PutInt32(lastLeaf)
        // store all the ptrs, n+1 of them
        // note loop bounds
        for q in start .. stop do
            pb.PutVarint(int64 (fst (children.[q])))
        // store all the keys, n of them
        // note loop bounds
        for q in start .. (stop-1) do
            let k = snd children.[q]
            if ((overflows <> null) && overflows.ContainsKey (q)) then
                pb.PutByte(FLAG_OVERFLOW)
                pb.PutVarint(int64 k.Length)
                pb.PutInt32(overflows.[q])
            else
                putArrayWithLength pb k

    let private calcAvailable pageSize currentSize couldBeRoot isBoundary =
        let basicSize = pageSize - currentSize
        let allowanceForBoundaryNode = if isBoundary then 4 else 0 // nextPage
        let allowanceForRootNode = if couldBeRoot then 2*4 else 0 // first/last Leaf
        basicSize - (allowanceForRootNode + allowanceForBoundaryNode)

    let private writeParentNodes firstLeaf lastLeaf (children:System.Collections.Generic.List<int32 * byte[]>) (pageManager:IPages) startingPageNumber startingBoundaryPageNumber fs (pb:PageBuilder) (pbOverflow:PageBuilder) =
        let nextGeneration = new System.Collections.Generic.List<int32 * byte[]>()
        let overflows = new System.Collections.Generic.Dictionary<int,int32>()
        // TODO encapsulate mutables in a class?
        let mutable sofar = 0
        let mutable nextPageNumber = startingPageNumber
        let mutable boundaryPageNumber = startingBoundaryPageNumber
        let mutable first = 0
        // assert children.Count > 1
        for i in 0 .. children.Count-1 do
            let (pagenum,k) = children.[i]
            let neededForInline = 1 + Varint.SpaceNeededFor (int64 k.Length) + k.Length + Varint.SpaceNeededFor (int64 pagenum)
            let neededForOverflow = 1 + Varint.SpaceNeededFor (int64 k.Length) + 4 + Varint.SpaceNeededFor (int64 pagenum)
            let isLastChild = (i = (children.Count - 1))
            if (sofar > 0) then
                let isBoundary = (nextPageNumber = boundaryPageNumber)
                let couldBeRoot = (nextGeneration.Count = 0)
                let avail = calcAvailable (pb.PageSize) sofar couldBeRoot isBoundary
                let fitsInline = (avail >= neededForInline)
                let wouldFitInlineOnNextPage = ((pb.PageSize - PARENT_NODE_HEADER_SIZE) >= neededForInline)
                let fitsOverflow = (avail >= neededForOverflow)
                let flushThisPage = isLastChild || ((not fitsInline) && (wouldFitInlineOnNextPage || (not fitsOverflow))) 
                let isRootNode = isLastChild && couldBeRoot

                if flushThisPage then
                    let thisPageNumber = nextPageNumber
                    let flags:byte = if isRootNode then FLAG_ROOT_NODE else if isBoundary then FLAG_BOUNDARY_NODE else 0uy
                    buildParentPage flags firstLeaf lastLeaf overflows pb children i first
                    if not isRootNode then
                        if isBoundary then
                            let newRange = pageManager.GetRange()
                            nextPageNumber <- fst newRange
                            boundaryPageNumber <- snd newRange
                            pb.SetBoundaryNextPageField(nextPageNumber)
                        else
                            nextPageNumber <- nextPageNumber + 1
                    pb.Flush(fs)
                    if nextPageNumber <> (thisPageNumber+1) then utils.SeekPage(fs, pb.PageSize, nextPageNumber)
                    nextGeneration.Add(thisPageNumber, snd children.[i-1])
                    sofar <- 0
                    first <- 0
                    overflows.Clear()
            if not isLastChild then 
                if sofar = 0 then
                    first <- i
                    overflows.Clear()
                    // 2 for the page type and flags
                    // 2 for the stored count
                    // 5 for the extra ptr we will add at the end, a varint, 5 is worst case
                    sofar <- 2 + 2 + 5
                let isBoundary = (nextPageNumber = boundaryPageNumber)
                if calcAvailable (pb.PageSize) sofar (nextGeneration.Count = 0) isBoundary >= neededForInline then
                    sofar <- sofar + k.Length
                else
                    let keyOverflowFirstPage = nextPageNumber
                    let kRange = writeOverflow pageManager nextPageNumber boundaryPageNumber pbOverflow fs (new MemoryStream(k))
                    nextPageNumber <- fst kRange
                    boundaryPageNumber <- snd kRange
                    sofar <- sofar + 4
                    overflows.[i] <- keyOverflowFirstPage
                // inline or not, we need space for the following things
                sofar <- sofar + 1 + Varint.SpaceNeededFor(int64 k.Length) + Varint.SpaceNeededFor(int64 pagenum)
        (nextPageNumber,boundaryPageNumber,nextGeneration)

    let Create(fs:Stream, pageSize:int, pageManager:IPages, csr:ICursor) :int32 = 
        let pb = new PageBuilder(pageSize)
        let pbOverflow = new PageBuilder(pageSize)
        // TODO encapsulate mutables in a class?
        let range = pageManager.GetRange()
        let mutable nextPageNumber:int32 = fst range
        let mutable boundaryPageNumber:int32 = snd range
        let mutable prevPageNumber:int32 = 0
        let mutable countPairs = 0
        let mutable lastKey:byte[] = null
        let mutable nodelist = new System.Collections.Generic.List<int32 * byte[]>()
        utils.SeekPage(fs, pb.PageSize, nextPageNumber)
        csr.First()
        while csr.IsValid() do
            let k = csr.Key()
            let v = csr.Value()
            // assert k <> null
            // but v might be null (a tombstone)
            let vlen = if v<>null then v.Length else int64 0

            let neededForOverflowPageNumber = 4 // TODO sizeof int

            let neededForKeyBase = 1 + Varint.SpaceNeededFor(int64 k.Length)
            let neededForKeyInline = neededForKeyBase + k.Length
            let neededForKeyOverflow = neededForKeyBase + neededForOverflowPageNumber

            let neededForValueInline = 1 + if v<>null then Varint.SpaceNeededFor(int64 vlen) + int vlen else 0
            let neededForValueOverflow = 1 + if v<>null then Varint.SpaceNeededFor(int64 vlen) + neededForOverflowPageNumber else 0

            let neededForBothInline = neededForKeyInline + neededForValueInline
            let neededForKeyInlineValueOverflow = neededForKeyInline + neededForValueOverflow
            let neededForBothOverflow = neededForKeyOverflow + neededForValueOverflow

            csr.Next()

            if pb.Position > 0 then
                let avail = pb.Available - (if nextPageNumber = boundaryPageNumber then 4 else 0)
                let fitBothInline = (avail >= neededForBothInline)
                let wouldFitBothInlineOnNextPage = ((pb.PageSize - LEAF_HEADER_SIZE) >= neededForBothInline)
                let fitKeyInlineValueOverflow = (avail >= neededForKeyInlineValueOverflow)
                let fitBothOverflow = (avail >= neededForBothOverflow)
                let flushThisPage = (not fitBothInline) && (wouldFitBothInlineOnNextPage || ( (not fitKeyInlineValueOverflow) && (not fitBothOverflow) ) )

                if flushThisPage then
                    // note similar flush code below at the end of the loop
                    let thisPageNumber = nextPageNumber
                    // assert -- it is not possible for this to be the last leaf.  so, at
                    // this point in the code, we can be certain that there is going to be
                    // another page.
                    if thisPageNumber = boundaryPageNumber then
                        pb.SetPageFlag FLAG_BOUNDARY_NODE
                        let newRange = pageManager.GetRange()
                        nextPageNumber <- fst newRange
                        boundaryPageNumber <- snd newRange
                        pb.SetBoundaryNextPageField(nextPageNumber)
                    else
                        nextPageNumber <- nextPageNumber + 1
                    pb.PutInt16At (OFFSET_COUNT_PAIRS, countPairs)
                    pb.Flush(fs)
                    if nextPageNumber <> (thisPageNumber+1) then utils.SeekPage(fs, pb.PageSize, nextPageNumber)
                    nodelist.Add(thisPageNumber, lastKey)
                    prevPageNumber <- thisPageNumber
                    pb.Reset()
                    countPairs <- 0
                    lastKey <- null
            if pb.Position = 0 then
                // we are here either because we just flushed a page
                // or because this is the very first page
                countPairs <- 0
                lastKey <- null
                pb.PutByte(LEAF_NODE)
                pb.PutByte(0uy) // flags

                pb.PutInt32 (prevPageNumber) // prev page num.
                pb.PutInt16 (0) // number of pairs in this page. zero for now. written at end.
                // assert pb.Position is 8 (LEAF_HEADER_SIZE)
            (*
             * one of the following cases must now be true:
             * 
             * - both the key and val will fit
             * - key inline and overflow the val
             * - overflow both
             * 
             * note that we don't care about the case where the
             * val would fit if we overflowed the key.  if the key
             * needs to be overflowed, then we're going to overflow
             * the val as well, even if it would fit.
             * 
             * if bumping to the next page would help, we have
             * already done it above.
             * 
             *)
            let available = pb.Available - (if nextPageNumber = boundaryPageNumber then 4 else 0)
            if (available >= neededForBothInline) then
                putArrayWithLength pb k
                putStreamWithLength pb v vlen
            else
                if (available >= neededForKeyInlineValueOverflow) then
                    putArrayWithLength pb k
                else
                    let keyOverflowFirstPage = nextPageNumber
                    let kRange = writeOverflow pageManager nextPageNumber boundaryPageNumber pbOverflow fs (new MemoryStream(k))
                    nextPageNumber <- fst kRange
                    boundaryPageNumber <- snd kRange

                    pb.PutByte(FLAG_OVERFLOW)
                    pb.PutVarint(int64 k.Length)
                    pb.PutInt32(keyOverflowFirstPage)

                let valueOverflowFirstPage = nextPageNumber
                let vRange = writeOverflow pageManager nextPageNumber boundaryPageNumber pbOverflow fs v
                nextPageNumber <- fst vRange
                boundaryPageNumber <- snd vRange

                pb.PutByte(FLAG_OVERFLOW)
                pb.PutVarint(int64 vlen)
                pb.PutInt32(valueOverflowFirstPage)
            lastKey <- k
            countPairs <- countPairs + 1
        if pb.Position > 0 then
            // note similar flush code above
            let thisPageNumber = nextPageNumber
            if not (csr.IsValid()) && (0 = nodelist.Count) then
                () // TODO
                // this is the root page, even though it is a leaf
            else
                if thisPageNumber = boundaryPageNumber then
                    pb.SetPageFlag FLAG_BOUNDARY_NODE
                    let newRange = pageManager.GetRange()
                    nextPageNumber <- fst newRange
                    boundaryPageNumber <- snd newRange
                    pb.SetBoundaryNextPageField(nextPageNumber)
                else
                    nextPageNumber <- nextPageNumber + 1
            pb.PutInt16At (OFFSET_COUNT_PAIRS, countPairs)
            pb.Flush(fs)
            if nextPageNumber <> (thisPageNumber+1) then utils.SeekPage(fs, pb.PageSize, nextPageNumber)
            nodelist.Add(thisPageNumber, lastKey)
        if nodelist.Count > 0 then
            let firstLeaf = fst nodelist.[0]
            let lastLeaf = fst nodelist.[nodelist.Count-1]
            // now write the parent pages.
            // maybe more than one level of them.
            // keep writing until we have written a level which has only one node,
            // which is the root node.
            while nodelist.Count > 1 do
                let (newNextPageNumber,newBoundaryPageNumber,newNodelist) = writeParentNodes firstLeaf lastLeaf nodelist pageManager nextPageNumber boundaryPageNumber fs pb pbOverflow
                nextPageNumber <- newNextPageNumber
                boundaryPageNumber <- newBoundaryPageNumber
                nodelist <- newNodelist
            // assert nodelist.Count = 1
            fst nodelist.[0]
        else
            0

    type private myOverflowReadStream(_fs:Stream, pageSize:int, first:int, _len:int) =
        inherit Stream()
        let fs = _fs
        let len = _len
        let buf:byte[] = Array.zeroCreate pageSize
        let mutable currentPage = first
        let mutable sofarOverall = 0
        let mutable sofarThisPage = 0

        // TODO consider supporting seek

        let ReadPage() = 
            utils.SeekPage(fs, buf.Length, currentPage)
            utils.ReadFully(fs, buf, 0, buf.Length)
            // assert PageType is OVERFLOW
            sofarThisPage <- 0

        let isBoundary() = 0uy <> (buf.[1] &&& FLAG_BOUNDARY_NODE)
        let availableOnThisPage() =
            let allowanceForBoundary = if isBoundary() then 4 else 0
            (buf.Length - OVERFLOW_PAGE_HEADER_SIZE) - allowanceForBoundary

        let GetInt32At(at) :int =
            let a0 = uint64 buf.[at+0]
            let a1 = uint64 buf.[at+1]
            let a2 = uint64 buf.[at+2]
            let a3 = uint64 buf.[at+3]
            let r = (a0 <<< 24) ||| (a1 <<< 16) ||| (a2 <<< 8) ||| (a3 <<< 0)
            // assert r fits in a 32 bit signed int
            int r

        do ReadPage()

        override this.Length = int64 len
        override this.CanRead = sofarOverall < len

        override this.Read(ba,offset,wanted) =
            if sofarOverall >= len then
                0
            else    
                if (sofarThisPage >= availableOnThisPage()) then
                    if isBoundary() then
                        currentPage <- GetInt32At(buf.Length - 4)
                    else
                        currentPage <- currentPage + 1
                    ReadPage()
                let available = Math.Min (availableOnThisPage(), len - sofarOverall)
                let num = Math.Min (available, wanted)
                System.Array.Copy (buf, OVERFLOW_PAGE_HEADER_SIZE + sofarThisPage, ba, offset, num)
                sofarOverall <- sofarOverall + num
                sofarThisPage <- sofarThisPage + num
                num

        override this.CanSeek = false
        override this.CanWrite = false
        override this.SetLength(v) = raise (new NotSupportedException())
        override this.Flush() = raise (new NotSupportedException())
        override this.Seek(offset,origin) = raise (new NotSupportedException())
        override this.Write(buf,off,len) = raise (new NotSupportedException())
        override this.Position
            with get() = raise (new NotSupportedException())
            and set(value) = raise (new NotSupportedException())

    let private readOverflow len fs pageSize (firstPage:int) =
        let ostrm = new myOverflowReadStream(fs, pageSize, firstPage, len)
        utils.ReadAll(ostrm)

    type private myCursor(_fs:Stream, pageSize:int, _rootPage:int) =
        let fs = _fs
        let rootPage = _rootPage
        let pr = new PageReader(pageSize)

        let mutable currentPage:int = 0
        let mutable leafKeys:int[] = null
        let mutable countLeafKeys = 0 // only realloc leafKeys when it's too small
        let mutable previousLeaf:int = 0
        let mutable currentKey = -1

        let resetLeaf() =
            countLeafKeys <- 0
            previousLeaf <- 0
            currentKey <- -1

        let setCurrentPage (pagenum:int) = 
            currentPage <- pagenum
            resetLeaf()
            if 0 = currentPage then false
            else                
                if pagenum <= rootPage then
                    utils.SeekPage(fs, pr.PageSize, currentPage)
                    pr.Read(fs)
                    true
                else
                    false
                    
        let getFirstAndLastLeaf() = 
            if not (setCurrentPage rootPage) then raise (new Exception())
            if pr.PageType = LEAF_NODE then
                (rootPage, rootPage)
            else
                pr.Reset()
                if pr.GetByte() <> PARENT_NODE then 
                    raise (new Exception())
                let pflag = pr.GetByte()
                pr.GetInt16() |> ignore
                if 0uy = (pflag &&& FLAG_ROOT_NODE) then 
                    raise (new Exception())
                let first = pr.GetInt32()
                let last = pr.GetInt32()
                (first, last)
              
        let (firstLeaf, lastLeaf) = getFirstAndLastLeaf()

        let nextInLeaf() =
            if (currentKey+1) < countLeafKeys then
                currentKey <- currentKey + 1
                true
            else
                false

        let prevInLeaf() =
            if (currentKey > 0) then
                currentKey <- currentKey - 1
                true
            else
                false

        let skipKey() =
            let kflag = pr.GetByte()
            let klen = pr.GetVarint()
            if 0uy = (kflag &&& FLAG_OVERFLOW) then
                pr.Skip(int klen)
            else
                pr.Skip(4)

        let skipValue() =
            let vflag = pr.GetByte()
            let vlen = pr.GetVarint()
            if 0uy <> (vflag &&& FLAG_TOMBSTONE) then ()
            else if 0uy <> (vflag &&& FLAG_OVERFLOW) then pr.Skip(4)
            else pr.Skip(int vlen)

        let readLeaf() =
            resetLeaf()
            pr.Reset()
            if pr.GetByte() <> LEAF_NODE then 
                raise (new Exception())
            pr.GetByte() |> ignore
            previousLeaf <- pr.GetInt32()
            countLeafKeys <- pr.GetInt16() |> int
            // only realloc leafKeys if it's too small
            if leafKeys=null || leafKeys.Length<countLeafKeys then
                leafKeys <- Array.zeroCreate countLeafKeys
            for i in 0 .. (countLeafKeys-1) do
                leafKeys.[i] <- pr.Position
                skipKey()
                skipValue()

        let compareKeyInLeaf n other = 
            pr.SetPosition(leafKeys.[n])
            let kflag = pr.GetByte()
            let klen = pr.GetVarint() |> int
            if 0uy = (kflag &&& FLAG_OVERFLOW) then
                pr.Compare(klen, other)
            else
                let pagenum = pr.GetInt32()
                let k = readOverflow klen fs pr.PageSize pagenum
                ByteComparer.Compare k other

        let keyInLeaf n = 
            pr.SetPosition(leafKeys.[n])
            let kflag = pr.GetByte()
            let klen = pr.GetVarint() |> int
            if 0uy = (kflag &&& FLAG_OVERFLOW) then
                pr.GetArray(klen)
            else
                let pagenum = pr.GetInt32()
                let k = readOverflow klen fs pr.PageSize pagenum
                k

        let rec searchLeaf k min max sop le ge = 
            if max < min then
                match sop with
                | SeekOp.SEEK_EQ -> -1
                | SeekOp.SEEK_LE -> le
                | _ -> ge
            else
                let mid = (max + min) / 2
                let cmp = compareKeyInLeaf mid k
                if 0 = cmp then mid
                else if cmp<0  then searchLeaf k (mid+1) max sop mid ge
                else searchLeaf k min (mid-1) sop le mid

        let readParentPage() =
            pr.Reset()
            if pr.GetByte() <> PARENT_NODE then 
                raise (new Exception())
            let pflag = pr.GetByte()
            let count = pr.GetInt16()
            let ptrs:int[] = Array.zeroCreate (int (count+1))
            let keys:byte[][] = Array.zeroCreate (int count)
            if 0uy <> (pflag &&& FLAG_ROOT_NODE) then pr.Skip(2*4)
            for i in 0 .. int count do
                ptrs.[i] <-  pr.GetVarint() |> int
            for i in 0 .. int (count-1) do
                let kflag = pr.GetByte()
                let klen = pr.GetVarint() |> int
                if 0uy = (kflag &&& FLAG_OVERFLOW) then
                    keys.[i] <- pr.GetArray(klen)
                else
                    let pagenum = pr.GetInt32()
                    keys.[i] <- readOverflow klen fs pr.PageSize pagenum
            (ptrs,keys)

        // this is used when moving forward through the leaf pages.
        // we need to skip any overflow pages.  when moving backward,
        // this is not necessary, because each leaf has a pointer to
        // the leaf before it.
        let rec searchForwardForLeaf() = 
            let pt = pr.PageType
            if pt = LEAF_NODE then true
            else if pt = PARENT_NODE then 
                // if we bump into a parent node, that means there are
                // no more leaves.
                false
            else
                if pr.CheckPageFlag(FLAG_BOUNDARY_NODE) then
                    // this happens when an overflow didn't fit.  the
                    // skip field gets set to point to its boundary page
                    // instead of to the end of the overflow.
                    if setCurrentPage (pr.GetBoundaryNextPageField()) then
                        searchForwardForLeaf()
                    else
                        false
                else
                    pr.SetPosition(2) // offset of the pages_remaining
                    let skip = pr.GetInt32()
                    // the skip field in an overflow page should take us to
                    // whatever follows this overflow (which could be a leaf
                    // or a parent or another overflow) OR to the boundary
                    // page for this overflow (in the case where the overflow
                    // didn't fit)
                    if setCurrentPage (currentPage + skip) then
                        searchForwardForLeaf()
                    else
                        false

        let leafIsValid() =
            let ok = (leafKeys <> null) && (countLeafKeys > 0) && (currentKey >= 0) && (currentKey < countLeafKeys)
            ok

        let rec searchInParentPage k (ptrs:int[]) (keys:byte[][]) (i:int) :int =
            // TODO linear search?  really?
            if i < keys.Length then
                let cmp = ByteComparer.Compare k (keys.[int i])
                if cmp>0 then
                    searchInParentPage k ptrs keys (i+1)
                else
                    ptrs.[int i]
            else 0

        let rec search pg k sop =
            if setCurrentPage pg then
                if LEAF_NODE = pr.PageType then
                    readLeaf()
                    currentKey <- searchLeaf k 0 (countLeafKeys - 1) sop -1 -1
                    if SeekOp.SEEK_EQ <> sop then
                        if not (leafIsValid()) then
                            // if LE or GE failed on a given page, we might need
                            // to look at the next/prev leaf.
                            if SeekOp.SEEK_GE = sop then
                                let nextPage =
                                    if pr.CheckPageFlag(FLAG_BOUNDARY_NODE) then pr.GetBoundaryNextPageField()
                                    else currentPage + 1
                                if (setCurrentPage (nextPage) && searchForwardForLeaf ()) then
                                    readLeaf()
                                    currentKey <- 0
                            else
                                if 0 = previousLeaf then
                                    resetLeaf()
                                else if setCurrentPage previousLeaf then
                                    readLeaf()
                                    currentKey <- countLeafKeys - 1
                else if PARENT_NODE = pr.PageType then
                    let (ptrs,keys) = readParentPage()
                    let found = searchInParentPage k ptrs keys 0
                    if 0 = found then
                        search ptrs.[ptrs.Length - 1] k sop
                    else
                        search found k sop

        interface ICursor with
            member this.IsValid() =
                leafIsValid()

            member this.Seek(k,sop) =
                search rootPage k sop

            member this.Key() =
                keyInLeaf currentKey
            
            member this.Value() =
                pr.SetPosition(leafKeys.[currentKey])

                skipKey()

                let vflag = pr.GetByte()
                let vlen = pr.GetVarint() |> int
                if 0uy <> (vflag &&& FLAG_TOMBSTONE) then null
                else if 0uy <> (vflag &&& FLAG_OVERFLOW) then 
                    let pagenum = pr.GetInt32()
                    upcast (new myOverflowReadStream(fs, pr.PageSize, pagenum, vlen))
                else 
                    upcast (new MemoryStream(pr.GetArray (vlen)))

            member this.ValueLength() =
                pr.SetPosition(leafKeys.[currentKey])

                skipKey()

                let vflag = pr.GetByte()
                if 0uy <> (vflag &&& FLAG_TOMBSTONE) then -1
                else
                    let vlen = pr.GetVarint() |> int
                    vlen

            member this.KeyCompare(k) =
                compareKeyInLeaf currentKey k

            member this.First() =
                if setCurrentPage firstLeaf then
                    readLeaf()
                    currentKey <- 0

            member this.Last() =
                if setCurrentPage lastLeaf then
                    readLeaf()
                    currentKey <- countLeafKeys - 1

            member this.Next() =
                if not (nextInLeaf()) then
                    let nextPage =
                        if pr.CheckPageFlag(FLAG_BOUNDARY_NODE) then pr.GetBoundaryNextPageField()
                        else currentPage + 1
                    if setCurrentPage (nextPage) && searchForwardForLeaf() then
                        readLeaf()
                        currentKey <- 0

            member this.Prev() =
                if not (prevInLeaf()) then
                    if 0 = previousLeaf then
                        resetLeaf()
                    else if setCurrentPage previousLeaf then
                        readLeaf()
                        currentKey <- countLeafKeys - 1
    
    let OpenCursor(fs, pageSize:int, rootPage:int) :ICursor =
        upcast (new myCursor(fs, pageSize, rootPage))

