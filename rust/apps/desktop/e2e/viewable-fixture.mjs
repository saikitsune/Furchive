/** Test-provider DTO upgrade only, not production URL validation or native proof.
 * Historical fixtures omit full_url; explicit null/noncanonical values model
 * Rust-refused media. New DTOs include the Rust-derived viewable_ids projection.
 */
export function viewableFixture(value){
 if(!value||typeof value!=='object')return value;
 const ids=posts=>posts.filter(p=>p.full_url===undefined||typeof p.full_url==='string'&&/^https:\/\/static[123]\.e621\.net\/data\/[a-f0-9]{2}\/[a-f0-9]{2}\/[a-f0-9]{32}\.(png|jpg|jpeg|gif|apng|webp|webm|mp4)$/.test(p.full_url)).map(p=>p.key.id);
 if(Array.isArray(value.items))return {...value,viewable_ids:value.viewable_ids??ids(value.items)};
 if(Array.isArray(value.entries))return {...value,viewable_ids:value.viewable_ids??ids(value.entries.flatMap(e=>e.post?[e.post]:[]))};
 if('selected_id'in value){const search=viewableFixture(value.search),pool=viewableFixture(value.pool);return {...value,search,pool,viewable_ids:value.viewable_ids??(search??pool)?.viewable_ids??[]};}
 return value;
}
