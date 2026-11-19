// GIFs from GIPHY, asked for by the reader's own browser: GIPHY's terms want its API called from the client, and
// forbid keeping its media addresses, so comments store an id and the address is looked up here each time.
const api = "https://api.giphy.com/v1/gifs";

function picture(gif) {
    const image = gif.images?.fixed_height ?? gif.images?.downsized ?? gif.images?.original;
    return image ? { id: gif.id, title: gif.title ?? "", url: image.webp ?? image.url, width: Number(image.width) || 0, height: Number(image.height) || 0 } : null;
}

async function ask(url) {
    const response = await fetch(url, { referrerPolicy: "no-referrer" });
    if (!response.ok) {
        throw new Error(`GIPHY answered ${response.status}`);
    }
    return ((await response.json()).data ?? []).map(picture).filter(p => p !== null);
}

// GIFs for some words, or the trending ones without any.
export function search(key, rating, words) {
    const common = `api_key=${encodeURIComponent(key)}&limit=24&rating=${encodeURIComponent(rating)}`;
    return ask(words ? `${api}/search?${common}&q=${encodeURIComponent(words)}` : `${api}/trending?${common}`);
}

// The pictures of the GIFs with these ids; one that's gone from GIPHY is simply missing from the answer.
export function resolve(key, ids) {
    return ids.length === 0 ? Promise.resolve([]) : ask(`${api}?api_key=${encodeURIComponent(key)}&ids=${ids.map(encodeURIComponent).join(",")}`);
}
