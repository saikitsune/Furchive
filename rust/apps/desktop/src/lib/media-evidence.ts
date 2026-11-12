// Runtime observations only. NEVER call this a host screenshot / animated frame proof.
export type MediaEvidence={schema:number;ok:boolean;engine:string;dataset:{total:number;unique:number;rowHeight:number};gallery:{initialMs:number;samples:Array<{elapsedMs:number;mounted:number;start:number;end:number;scrollTop:number}>;selected:string;focused:boolean};images:Array<{name:string;status:string;released:boolean}>;videos:Array<{name:string;played:boolean;seeked:boolean;speed:number;muted:boolean;time:number;released:boolean}>;lifecycle:Array<{cycle:number;released:boolean;staleIgnored:boolean;closed:boolean;remaining:number}>;animationCheckpoints:string[]};
export function validateMediaEvidence(r:MediaEvidence) {
 const demand=(ok:unknown,name:string)=>{if(!ok)throw new Error('media evidence: '+name);};
 const bounded=(n:number,max:number)=>Number.isFinite(n)&&n>=0&&n<=max;
 demand(r?.schema===1&&r.ok===true&&typeof r.engine==='string'&&r.engine.length>0,'identity');
 demand(r.dataset?.total===10000&&r.dataset.unique===10000&&r.dataset.rowHeight===184,'dataset');
 demand(bounded(r.gallery?.initialMs,1000),'initial render <= 1000ms');
 demand(r.gallery.samples?.length===6,'six scroll samples');
 for(const s of r.gallery.samples){demand(bounded(s.elapsedMs,250),'scroll <= 250ms');demand(Number.isInteger(s.mounted)&&s.mounted>=1&&s.mounted<=108&&s.end-s.start===s.mounted&&s.start>=0&&s.end<=10000&&bounded(s.scrollTop,2_000_000),'bounded DOM');}
 demand(r.gallery.samples.some(s=>s.start>0)&&r.gallery.selected==='fixture-9999'&&r.gallery.focused===true,'selection and focus');
 const imageNames=['viewer-large.png','viewer-corrupt.png','viewer-truncated.png','viewer-oversized.png'];
 demand(r.images?.length===imageNames.length,'image inventory');
 for(const [i,name]of imageNames.entries()){const row=r.images.find(x=>x.name===name);demand(row?.status===(i?'error':'ready')&&row.released===true,'image '+name);}
 const videoNames=['vp8.webm','vp9.webm','h264.mp4'];demand(r.videos?.length===3,'video inventory');
 for(const name of videoNames){const row=r.videos.find(x=>x.name===name);demand(row?.played===true&&row.seeked===true&&row.speed===2&&row.muted===true&&row.time>=0.45&&row.released===true,'video '+name);}
 demand(r.lifecycle?.length===20,'20 lifecycle cycles');
 for(const [i,row]of r.lifecycle.entries())demand(row.cycle===i&&row.released===true&&row.staleIgnored===true&&row.closed===true&&row.remaining===0,'cleanup cycle '+i);
 demand(JSON.stringify(r.animationCheckpoints)===JSON.stringify(['gif','apng','webp']),'host animation checkpoints required');
 return true;
}
