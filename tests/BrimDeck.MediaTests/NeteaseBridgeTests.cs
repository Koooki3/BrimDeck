using BrimDeck.Native;
using Jint;

// Runs the injected player bridge against a mocked client store in a JavaScript engine.
// Each Execute call ends by running pending promise jobs, which stands in for a turn of the page's event loop.
internal static class NeteaseBridgeTests
{
    public static void Run(Action<string, bool> check)
    {
        Engine Page()
        {
            var engine = new Engine();
            engine.SetValue("__check", new Action<bool, string>((ok, name) => check("Bridge: " + name, ok)));
            engine.SetValue("SCRIPT", NeteaseCdpConnection.BridgeScript);
            engine.Execute("""
                globalThis.window = globalThis;
                function check(test, name) { __check(!!test, name); }
                const run = () => (0, eval)(SCRIPT);
                """);
            return engine;
        }

        var page = Page();
        page.Execute("""
            const actions=[], pushes=[], listeners={}, subscribers=[];
            const p={resourceTrackId:42,resourceName:'Song',resourceArtists:[{name:'Artist'}],curTrack:{duration:201000,album:{name:'Album'}},playingState:1,playingMode:'playCycle'};
            const store={getState:()=>({playing:p}),subscribe:f=>{subscribers.push(f);return()=>{}},dispatch:a=>actions.push(a)};
            const player={subscribePlayStatus:({type,callback})=>{(listeners[type]??=[]).push(callback)}};
            const loader={c:{one:{exports:{a:{getStore:()=>store.getState(),getDispatch:()=>store.dispatch,app:{_store:store}}}},two:{exports:{AudioPlayer:player}},three:{exports:{b:{lastPlaying:{get:()=>Promise.resolve({trackId:42,current:57})}}}}}};
            let clock=100000;
            globalThis.performance={now:()=>2000}; Date.now=()=>clock;
            globalThis.__brimDeckMediaPush=s=>pushes.push(JSON.parse(s));
            globalThis.webpackJsonp={push:entry=>{ for(const module of Object.values(entry[1])) module({}, {}, loader); }};
            globalThis._enterAppTime=1000;
            check(!run().ready && subscribers.length===0,'startup waits for client window initialization before attaching');
            delete globalThis._enterAppTime;
            let result=run();
            check(result.ready && result.snapshot.songId==='42' && result.snapshot.durationMs===201000,'existing playback store identified without DOM traversal');
            """);
        page.Execute("""
            check(pushes.at(-1).positionMs===57000,'initial paused position reads current-track storage only');
            result=run();
            check(listeners.playprogress.length===1 && subscribers.length===1,'reconnect does not add duplicate subscriptions');
            const cmd=__brimDeckMusicV2.command;
            check(cmd({command:'seek',seconds:80,songId:'42'}).dispatched && actions.at(-1).type==='playing/setPlayingPosition' && actions.at(-1).payload.duration===80,'seek dispatch uses seconds');
            const before=actions.length;
            check(!cmd({command:'seek',seconds:80,songId:'43'}).dispatched && actions.length===before,'stale-song seek rejected before dispatch');
            check(!cmd({command:'seek',seconds:202,songId:'42'}).dispatched,'seek beyond duration rejected');
            check(cmd({command:'shuffle'}).dispatched && actions.at(-1).payload.playingMode==='playRandom','shuffle maps to client playback mode');
            check(cmd({command:'repeat'}).dispatched && actions.at(-1).payload.playingMode==='playOneCycle','list loop advances to single loop');
            for (const mode of ['playCycle', 'playOneCycle', 'playRandom', 'playOrder'])
              check(cmd({command:'mode', mode}).dispatched && actions.at(-1).payload.playingMode===mode,'combined mode control selects '+mode);
            check(cmd({command:'toggle'}).dispatched && actions.at(-1).type==='playing/resume','paused toggle dispatches resume');
            p.playingState=2;
            check(cmd({command:'toggle'}).dispatched && actions.at(-1).type==='playing/pause','playing toggle dispatches pause');
            check(!cmd({command:'deleteAccount'}).dispatched,'unknown command rejected');
            p.playingMode='playRandom';
            check(cmd({command:'repeat'}).dispatched && actions.at(-1).payload.playingMode==='playCycle','repeat from shuffle advances from none to list');
            p.playingMode='playFm';
            check(!cmd({command:'mode',mode:'playRandom'}).dispatched,'combined mode control is unavailable in private FM');
            check(!cmd({command:'previous'}).dispatched && !cmd({command:'shuffle'}).dispatched && !cmd({command:'repeat'}).dispatched,'FM rejects previous and unsupported modes');
            p.playingMode='playCycle';
            listeners.seek[0]({playId:'42_audio',position:80,code:0});
            check(pushes.at(-1).positionMs===80000,'real seek event updates timeline');
            listeners.seek[0]({playId:'41_old',position:150,code:0});
            check(pushes.at(-1).positionMs===80000,'old track event cannot corrupt current progress');
            p.resourceTrackId=43;p.resourceName='Next';subscribers[0]();
            check(pushes.at(-1).positionMs===null && pushes.at(-1).songId==='43','track change clears old time anchor');
            __brimDeckMusicV2.detach('__brimDeckMediaPush');const previous=pushes.length;
            listeners.seek[0]({playId:'43_audio',position:30,code:0});
            check(pushes.length===previous,'detached bridge sends no events');
            let activePushes=0,stalePushes=0;
            globalThis.activeClient=()=>activePushes++; globalThis.crashedClient=()=>stalePushes++;
            const bridge=__brimDeckMusicV2;
            bridge.attach('activeClient');bridge.attach('crashedClient');
            const staleCount=stalePushes;
            clock+=60000;bridge.keepalive('activeClient');clock+=40000;bridge.keepalive('activeClient');
            const activeCount=activePushes;
            listeners.seek[0]({playId:'43_audio',position:31,code:0});
            check(stalePushes===staleCount && activePushes===activeCount+1,'expired crashed client is removed without disrupting a live client');
            check(bridge.attach('crashedClient').ready && subscribers.length===1 && listeners.playprogress.length===1,'expired client reconnects without duplicating native subscriptions');
            let restarted=true;
            for(let i=0;i<100;i++) { clock+=100000;bridge.keepalive('activeClient');const name='restart'+i;globalThis[name]=()=>{};restarted&&=bridge.attach(name).ready; }
            check(restarted,'repeated abnormal exits cannot exhaust the client subscription limit');
            """);

        // After a restart the client reports the paused state before the restored track id.
        var restored = Page();
        restored.Execute("""
            const pushes=[], subscribers=[];
            const q={resourceName:'',playingState:1,playingMode:'playCycle',curTrack:{duration:201000,album:{}}};
            const store={getState:()=>({playing:q}),subscribe:f=>{subscribers.push(f);return()=>{}},dispatch:()=>{}};
            const loader={c:{one:{exports:{a:{getStore:()=>store.getState(),getDispatch:()=>store.dispatch,app:{_store:store}}}},three:{exports:{b:{lastPlaying:{get:()=>Promise.resolve({trackId:'77',current:39})}}}}}};
            globalThis.performance={now:()=>2000}; Date.now=()=>1;
            globalThis.__brimDeckMediaPush=s=>pushes.push(JSON.parse(s));
            globalThis.webpackJsonp={push:entry=>{ for(const module of Object.values(entry[1])) module({}, {}, loader); }};
            check(run().ready,'bridge attaches while the restored track id is still missing');
            """);
        restored.Execute("""
            check(pushes.at(-1).positionMs===null,'no saved position is taken without a track id');
            q.resourceTrackId='77'; q.resourceName='Restored'; subscribers[0]();
            """);
        restored.Execute("check(pushes.at(-1).positionMs===39000 && pushes.at(-1).songId==='77','saved paused position is read once the restored track id arrives');");

        // Liking uses the client's own liked songs and toggle; a bridge left by an earlier BrimDeck stops pushing.
        var liking = Page();
        liking.Execute("""
            const actions=[], pushes=[], subscribers=[], released=[];
            const s={playing:{resourceTrackId:'42',resourceName:'Song',resourceType:'track',resourceArtists:[{name:'Artist'}],curTrack:{duration:200000,album:{name:'Album'}},playingState:2,playingMode:'playCycle'},
              'async:hostResource':{likeTracksMap:{'42':true}},host:{uid:7},app:{isOnline:true}};
            const store={getState:()=>s,subscribe:f=>{subscribers.push(f);return()=>{}},dispatch:a=>actions.push(a)};
            const loader={c:{one:{exports:{a:{getStore:()=>store.getState(),getDispatch:()=>store.dispatch,app:{_store:store}}}}}};
            globalThis.performance={now:()=>2000}; Date.now=()=>1;
            globalThis.__brimDeckMediaPush=x=>pushes.push(JSON.parse(x));
            globalThis.__brimDeckMediaPush_0123456789abcdef0123456789abcdef=()=>{};
            globalThis.__brimDeckMusicV1={version:1,detach:name=>released.push(name)};
            globalThis.webpackJsonp={push:entry=>{ for(const module of Object.values(entry[1])) module({}, {}, loader); }};
            const result=run();
            check(result.ready && result.snapshot.canLike && result.snapshot.liked,'a liked song is read from the client liked songs');
            check(released.includes('__brimDeckMediaPush') && released.includes('__brimDeckMediaPush_0123456789abcdef0123456789abcdef'),'an earlier bridge releases its clients');
            check(__brimDeckMusicV2.version===2 && __brimDeckMusicV1.version===1,'the new bridge installs beside the earlier one');
            const cmd=__brimDeckMusicV2.command;
            check(cmd({command:'like',songId:'42'}).dispatched && actions.at(-1).type==='async:hostResource/setLikeCurPlayingTrack','like dispatches the client toggle for the current song');
            const count=actions.length;
            check(!cmd({command:'like',songId:'41'}).dispatched && actions.length===count,'like for a song that is no longer playing is rejected');
            s['async:hostResource'].likeTracksMap={}; subscribers[0]();
            check(pushes.at(-1).canLike && pushes.at(-1).liked===false,'a song unliked in the client is pushed');
            s.app.isOnline=false; subscribers[0]();
            check(!pushes.at(-1).canLike && !cmd({command:'like',songId:'42'}).dispatched,'an offline client cannot like');
            s.app.isOnline=true; s.host={}; subscribers[0]();
            check(!pushes.at(-1).canLike,'a signed-out client cannot like');
            s.host={uid:7}; s.playing.resourceType='voice'; subscribers[0]();
            check(!pushes.at(-1).canLike,'podcast episodes are not liked as songs');
            """);
    }
}
