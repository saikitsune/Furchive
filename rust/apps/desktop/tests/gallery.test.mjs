import {test} from 'node:test';
import assert from 'node:assert/strict';
import {visibleRange} from '../src/lib/gallery.js';
test('gallery mounts only viewport rows plus overscan for ten thousand items',()=>{
 const r=visibleRange(10000,5,10000,600,220,2);
 assert.equal(r.start,215); assert.equal(r.end,255);
});
