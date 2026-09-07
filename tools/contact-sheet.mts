/** 13体を並べた確認用のPNGを作る。目視で見てもらうためだけのもの。 */
import { deflateSync } from "node:zlib";
import { writeFileSync } from "node:fs";
import { MASCOT_PALETTE } from "../sprites/palette.ts";
import { renderPose, SPRITE_SIZE } from "../sprites/sprite.ts";
import { BODIES } from "../sprites/bodies.ts";
import { MOTIONS } from "../sprites/motions.ts";
import { CHARACTERS } from "../sprites/characters.ts";

const SCALE = Number(process.env.SCALE ?? 5);
const BG = [0x1c, 0x20, 0x2a];
const PAD = 6;
function crc32(b: Buffer){let c,crc=0xffffffff;for(let n=0;n<b.length;n++){c=(crc^b[n])&0xff;for(let k=0;k<8;k++)c=c&1?0xedb88320^(c>>>1):c>>>1;crc=c^(crc>>>8);}return (crc^0xffffffff)>>>0;}
function chunk(t:string,d:Buffer){const l=Buffer.alloc(4);l.writeUInt32BE(d.length);const b=Buffer.concat([Buffer.from(t,"ascii"),d]);const c=Buffer.alloc(4);c.writeUInt32BE(crc32(b));return Buffer.concat([l,b,c]);}
function png(path:string,w:number,h:number,rgb:Buffer){const raw=Buffer.alloc((w*3+1)*h);for(let y=0;y<h;y++){raw[y*(w*3+1)]=0;rgb.copy(raw,y*(w*3+1)+1,y*w*3,(y+1)*w*3);}const ih=Buffer.alloc(13);ih.writeUInt32BE(w,0);ih.writeUInt32BE(h,4);ih[8]=8;ih[9]=2;writeFileSync(path,Buffer.concat([Buffer.from([0x89,0x50,0x4e,0x47,0x0d,0x0a,0x1a,0x0a]),chunk("IHDR",ih),chunk("IDAT",deflateSync(raw)),chunk("IEND",Buffer.alloc(0))]));}

const pal = MASCOT_PALETTE.map(h => h === "transparent" ? null : [1,3,5].map(i=>parseInt(h.slice(i,i+2),16)));
const ids = Object.keys(BODIES);
const motionIds = Object.keys(MOTIONS);
const cell = SPRITE_SIZE*SCALE + PAD*2;
const W = motionIds.length*cell, H = ids.length*cell;
const img = Buffer.alloc(W*H*3);
for (let i=0;i<W*H;i++) img.set(BG, i*3);
ids.forEach((id,r)=>motionIds.forEach((m,c)=>{
  const g = renderPose(MOTIONS[m].frames[0], BODIES[id]);
  for (let y=0;y<SPRITE_SIZE;y++) for (let x=0;x<SPRITE_SIZE;x++){
    const col = pal[g[y*SPRITE_SIZE+x]]; if(!col) continue;
    for (let sy=0;sy<SCALE;sy++) for (let sx=0;sx<SCALE;sx++)
      img.set(col, (((r*cell+PAD+y*SCALE+sy)*W)+(c*cell+PAD+x*SCALE+sx))*3);
  }
}));
png(process.argv[2] ?? "/tmp/cast.png", W, H, img);
console.log(`${process.argv[2]}  ${W}x${H}`);
console.log("行:", ids.map(i=>CHARACTERS.find(c=>c.id===i)?.name.ja ?? i).join(" / "));
console.log("列:", motionIds.join(" / "));
