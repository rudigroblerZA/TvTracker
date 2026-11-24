// Turns the picture someone picked into a small square JPEG, in the browser, before it is uploaded: the middle of the
// picture, scaled to `size` pixels a side. The service keeps what it is sent, so the shrinking happens here.
// Returns the JPEG's bytes, which .NET reads as a stream (a picture is too big for one interop message), or null when
// nothing was picked; throws when the file isn't a picture the browser can read.
export async function squared(input, size) {
    const file = input.files && input.files[0];
    input.value = "";
    if (!file) return null;
    const bitmap = await createImageBitmap(file);
    try {
        const side = Math.min(bitmap.width, bitmap.height);
        const canvas = document.createElement("canvas");
        canvas.width = canvas.height = size;
        const context = canvas.getContext("2d");
        // A see-through picture gets white behind it, since JPEG has no transparency.
        context.fillStyle = "#fff";
        context.fillRect(0, 0, size, size);
        context.imageSmoothingQuality = "high";
        context.drawImage(bitmap, (bitmap.width - side) / 2, (bitmap.height - side) / 2, side, side, 0, 0, size, size);
        const blob = await new Promise(resolve => canvas.toBlob(resolve, "image/jpeg", 0.85));
        return new Uint8Array(await blob.arrayBuffer());
    } finally {
        bitmap.close();
    }
}
