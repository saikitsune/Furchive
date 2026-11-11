import {test} from 'node:test';
import assert from 'node:assert/strict';
import {validateQualification} from '../src/lib/qualification.js';
test('native evidence refuses decoded-but-unplayed videos and unbounded gallery',()=>{
 assert.throws(()=>validateQualification({images:[{ok:true}],videos:[{ok:true,time:0,seeked:false}],gallery:{total:10000,mounted:10000}}));
});
test('complete playing and seeked native receipt passes',()=>{
 const result={engine:'native-test',images:['still.jpg','still.png','animation.gif','animation.apng','still.webp'].map(name=>({name,ok:true,width:320,height:200})),videos:['vp8.webm','vp9.webm','h264.mp4'].map(name=>({name,ok:true,time:0.9,seeked:true,played:true})),gallery:{total:10000,mounted:40,start:440,end:480,scrollTop:20000}};
 assert.equal(validateQualification(result),true);
});
test('native evidence requires complete codec inventory',()=>{
 assert.throws(()=>validateQualification({images:[],videos:[],gallery:{total:10000,mounted:40}}));
});
