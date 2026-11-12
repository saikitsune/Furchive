export type Rating='safe'|'questionable'|'explicit';
export type RatingFilter='any'|{only:Rating[]};
export type Query={include:string[];exclude:string[];ratings:RatingFilter;artist:string|null;sort:'newest'|'oldest'|'score'|'favorites'|'random'};
export type Post={key:{source:string;id:string};title:string;description:string;rating:Rating;tags:Record<string,string[]>;artists:string[];kind:unknown;preview_url:string|null;full_url:string|null;score:number;favorite_count:number};
export type SearchPage={items:Post[];next_page:number|null;raw_count:number};
export type SavedSearch={id:string;name:string;query:Query};
export type PoolPage={pool:{key:{source:string;id:string};name:string;description:string|null;post_ids:string[]};entries:{id:string;position:number;post:Post|null}[];total:number;next_offset:number|null};
export type Suggestion={name:string;category:unknown;post_count:number};
export type Preferences={filename_template:string;pool_filename_template:string;concurrent_downloads:number;duplicates:'skip'|'overwrite'|'rename';gallery_scale:number;cache_limit_bytes:number;restore_session:boolean;save_metadata:boolean;start_muted:boolean};
export type Invoke=<T>(command:string,args?:Record<string,unknown>)=>Promise<T>;
export class BrowserBridge {
 private invoke:Invoke;
 constructor(invoke:Invoke){this.invoke=invoke;}
 search(query:Query,page:number){return this.invoke<SearchPage>('search',{query,page});}
 favorites(query:Query,username:string,page:number){return this.invoke<SearchPage>('favorites',{query,username,page});}
 pool(id:number,offset:number,ratings:RatingFilter){return this.invoke<PoolPage>('pool_page',{id,offset,limit:50,ratings});}
 suggestions(prefix:string){return this.invoke<Suggestion[]>('suggestions',{prefix});}
 preferences(){return this.invoke<Preferences>('preferences');}
 savePreferences(preferences:Preferences){return this.invoke<Preferences>('save_preferences',{preferences});}
 searches(){return this.invoke<SavedSearch[]>('saved_searches');}
 save(search:SavedSearch){return this.invoke<void>('save_search',{search});}
 delete(id:string){return this.invoke<void>('delete_search',{id});}
 pins(){return this.invoke<{source:string;id:string}[]>('pinned_pools');}
 pin(id:number,pinned:boolean){return this.invoke<void>('set_pin',{id,pinned});}
}
