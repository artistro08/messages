// Renders the app's icons from assets-src into src/GMessages.App/Assets (run by scripts/Make-Icons.ps1). Ported from
// Leaf Calendar's tools/make-icons.cjs:
//   - every MSIX visual asset (Square44x44Logo with its target sizes, Square150x150Logo, Wide310x150Logo, StoreLogo, each
//     at scale 100/125/150/200/400) and the window/exe icon (AppIcon.ico) from Messages.svg;
//   - the tray icons (TrayLight.ico, black for light taskbars; TrayDark.ico, white for dark ones) from Messages-mono.svg,
//     at every small-icon size Windows uses from 100% to 400% display scale, so the taskbar never scales one.
// Each logo image is drawn at least 1024 px across and averaged down to its exact size, so its curves stay smooth at
// taskbar sizes. The tray outline is a plain shape and is drawn at its exact size.
// Usage: node make-icons.cjs <assets-src> <Assets>   (needs @resvg/resvg-js resolvable from the working folder)

const fs = require('fs');
const path = require('path');
const zlib = require('zlib');
const { Resvg } = require(require.resolve('@resvg/resvg-js', { paths: [process.cwd()] }));
const [sourceDir, assets] = process.argv.slice(2);

// =========================================================================
// MAIN ICON
// =========================================================================

// The logo's inner markup, so it can be placed on a larger canvas
const source = fs.readFileSync(path.join(sourceDir, 'Messages.svg'), 'utf8');
const open = source.match(/<svg\b[^>]*>/)[0];
const viewBox = open.match(/viewBox="([^"]+)"/)[1];
const style = (open.match(/style="([^"]+)"/) || [, ''])[1];
const inner = source.slice(source.indexOf(open) + open.length, source.lastIndexOf('</svg>'));
const namespaces = (open.match(/xmlns(:\w+)?="[^"]*"/g) || []).join(' ');

// How big the logo is drawn before it's averaged down to its size
const drawAt = 1024;

// The logo at size `size`, centered on a transparent w x h canvas: drawn k times larger, then each k x k block of
// (premultiplied) pixels averaged into one. Returns premultiplied RGBA pixels, like the renderer's own images
function render(w, h, size) {
    const k = Math.max(1, Math.ceil(drawAt / size));
    const x = (w - size) / 2 * k, y = (h - size) / 2 * k;
    const svg = `<svg ${namespaces} width="${w * k}" height="${h * k}" viewBox="0 0 ${w * k} ${h * k}">` +
        `<svg x="${x}" y="${y}" width="${size * k}" height="${size * k}" viewBox="${viewBox}" style="${style}">${inner}</svg></svg>`;
    const big = new Resvg(svg, { fitTo: { mode: 'original' } }).render().pixels;
    const pixels = Buffer.alloc(w * h * 4);
    for (let y0 = 0; y0 < h; y0++) {
        for (let x0 = 0; x0 < w; x0++) {
            const sum = [0, 0, 0, 0];
            for (let dy = 0; dy < k; dy++) {
                const row = ((y0 * k + dy) * w * k + x0 * k) * 4;
                for (let i = 0; i < k * 4; i++) {
                    sum[i % 4] += big[row + i];
                }
            }

            for (let c = 0; c < 4; c++) {
                pixels[(y0 * w + x0) * 4 + c] = Math.round(sum[c] / (k * k));
            }
        }
    }

    return pixels;
}

// Premultiplied RGBA pixels as a PNG (straight alpha, no filtering, deflated)
function png(pixels, w, h) {
    const raw = Buffer.alloc((w * 4 + 1) * h);
    for (let y = 0; y < h; y++) {
        for (let x = 0; x < w; x++) {
            const s = (y * w + x) * 4, d = y * (w * 4 + 1) + 1 + x * 4, a = pixels[s + 3];
            for (let c = 0; c < 3; c++) {
                raw[d + c] = a === 0 ? 0 : Math.min(255, Math.round(pixels[s + c] * 255 / a));
            }

            raw[d + 3] = a;
        }
    }

    const chunk = (type, data) => {
        const head = Buffer.alloc(8);
        head.writeUInt32BE(data.length, 0);
        head.write(type, 4, 'ascii');
        const crc = Buffer.alloc(4);
        crc.writeUInt32BE(zlib.crc32(Buffer.concat([head.subarray(4), data])), 0);
        return Buffer.concat([head, data, crc]);
    };
    const ihdr = Buffer.alloc(13);
    ihdr.writeUInt32BE(w, 0);
    ihdr.writeUInt32BE(h, 4);
    ihdr.set([8, 6, 0, 0, 0], 8);
    return Buffer.concat([
        Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
        chunk('IHDR', ihdr),
        chunk('IDAT', zlib.deflateSync(raw, { level: 9 })),
        chunk('IEND', Buffer.alloc(0)),
    ]);
}

function logo(w, h, size) {
    return png(render(w, h, size), w, h);
}

// =========================================================================
// ICO FILES
// =========================================================================

// Premultiplied pixels as an uncompressed 32-bit icon image (a BITMAPINFOHEADER with the height doubled, BGRA rows bottom
// up, then the AND mask, set where a pixel is clear so the tray keeps a clear background even where it ignores alpha):
// the taskbar and title bar show a generic icon for PNG-compressed images below 256 px. Icons aren't premultiplied, so
// each color is divided back out
function dib(px, size) {
    const rowBytes = size * 4, maskBytes = Math.ceil(size / 32) * 4;
    const buf = Buffer.alloc(40 + rowBytes * size + maskBytes * size);
    buf.writeUInt32LE(40, 0);
    buf.writeInt32LE(size, 4);
    buf.writeInt32LE(size * 2, 8);
    buf.writeUInt16LE(1, 12);
    buf.writeUInt16LE(32, 14);
    buf.writeUInt32LE(0, 16);
    buf.writeUInt32LE(rowBytes * size + maskBytes * size, 20);
    const maskStart = 40 + rowBytes * size;
    for (let y = 0; y < size; y++) {
        const src = (size - 1 - y) * rowBytes, dst = 40 + y * rowBytes;
        for (let x = 0; x < size; x++) {
            const s = src + x * 4, d = dst + x * 4, a = px[s + 3];
            const unmultiply = c => a === 0 ? 0 : Math.min(255, Math.round(c * 255 / a));
            buf[d] = unmultiply(px[s + 2]);
            buf[d + 1] = unmultiply(px[s + 1]);
            buf[d + 2] = unmultiply(px[s]);
            buf[d + 3] = a;
            if (a === 0) {
                buf[maskStart + y * maskBytes + (x >> 3)] |= 0x80 >> (x % 8);
            }
        }
    }
    return buf;
}

// An .ico of the given images (a 256 px entry is stored as size 0)
function writeIco(file, sizes, images) {
    const header = Buffer.alloc(6 + 16 * images.length);
    header.writeUInt16LE(0, 0);
    header.writeUInt16LE(1, 2);
    header.writeUInt16LE(images.length, 4);
    let offset = header.length;
    images.forEach((image, i) => {
        const entry = 6 + 16 * i, size = sizes[i];
        header.writeUInt8(size >= 256 ? 0 : size, entry);
        header.writeUInt8(size >= 256 ? 0 : size, entry + 1);
        header.writeUInt8(0, entry + 2);
        header.writeUInt8(0, entry + 3);
        header.writeUInt16LE(1, entry + 4);
        header.writeUInt16LE(32, entry + 6);
        header.writeUInt32LE(image.length, entry + 8);
        header.writeUInt32LE(offset, entry + 12);
        offset += image.length;
    });
    write(file, Buffer.concat([header, ...images]));
}

function write(file, data) {
    fs.writeFileSync(path.join(assets, file), data);
}

// =========================================================================
// OUTPUT
// =========================================================================

// Scaled Assets (tiles keep the logo at two thirds of the short side)
for (const scale of [100, 125, 150, 200, 400]) {
    const s = scale / 100, q = `.scale-${scale}.png`;
    write(`Square44x44Logo${q}`, logo(Math.round(44 * s), Math.round(44 * s), Math.round(44 * s)));
    write(`StoreLogo${q}`, logo(Math.round(50 * s), Math.round(50 * s), Math.round(50 * s)));
    write(`Square150x150Logo${q}`, logo(Math.round(150 * s), Math.round(150 * s), Math.round(100 * s)));
    write(`Wide310x150Logo${q}`, logo(Math.round(310 * s), Math.round(150 * s), Math.round(100 * s)));
}

// Taskbar And Start Sizes (plated, unplated, and light-mode unplated alike: the logo brings its own colors; without the
// light-mode set, a light taskbar scales another size, which leaves jagged edges)
for (const size of [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256]) {
    write(`Square44x44Logo.targetsize-${size}.png`, logo(size, size, size));
    write(`Square44x44Logo.targetsize-${size}_altform-unplated.png`, logo(size, size, size));
    write(`Square44x44Logo.targetsize-${size}_altform-lightunplated.png`, logo(size, size, size));
}

// Window And Exe Icon: one image per size, so Windows picks an exact one instead of scaling; uncompressed below 256
const icoSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
writeIco('AppIcon.ico', icoSizes, icoSizes.map(size => size >= 256 ? logo(size, size, size) : dib(render(size, size, size), size)));

// Tray Icons: the outline black for light taskbars, white for dark ones, at SM_CXSMICON for 100% to 400% scale
const traySizes = [16, 20, 24, 28, 32, 36, 40, 48, 56, 64];
const mono = fs.readFileSync(path.join(sourceDir, 'Messages-mono.svg'), 'utf8');
for (const [file, color] of [['TrayLight.ico', '#000000'], ['TrayDark.ico', '#FFFFFF']]) {
    const images = traySizes.map(size => {
        const shape = new Resvg(mono.replace('fill="#000000"', `fill="${color}"`), { fitTo: { mode: 'width', value: size } }).render();
        if (shape.width !== size || shape.height !== size) {
            throw new Error(`The tray outline rendered at ${shape.width} x ${shape.height}, not ${size}.`);
        }

        return dib(shape.pixels, size);
    });
    writeIco(file, traySizes, images);
}
