/**
 * Сжимает изображение на клиенте перед отправкой на сервер (для оцифровки медикамента по
 * фото): ресайзит по большей стороне до maxDimension и перекодирует в JPEG. Ускоряет обработку
 * локальной LLM — большие фото с телефона (4000px+) заметно замедляют vision-инференс.
 */
export function compressImage(file: File, maxDimension = 1920, quality = 0.85): Promise<Blob> {
  return new Promise((resolve, reject) => {
    const img = new Image();
    const objectUrl = URL.createObjectURL(file);

    img.onload = () => {
      URL.revokeObjectURL(objectUrl);

      const scale = Math.min(1, maxDimension / Math.max(img.width, img.height));
      const width = Math.round(img.width * scale);
      const height = Math.round(img.height * scale);

      const canvas = document.createElement('canvas');
      canvas.width = width;
      canvas.height = height;

      const ctx = canvas.getContext('2d');
      if (!ctx) {
        reject(new Error('Canvas 2D context недоступен.'));
        return;
      }
      ctx.drawImage(img, 0, 0, width, height);

      canvas.toBlob(
        (blob) => (blob ? resolve(blob) : reject(new Error('Не удалось сжать изображение.'))),
        'image/jpeg',
        quality,
      );
    };

    img.onerror = () => {
      URL.revokeObjectURL(objectUrl);
      reject(new Error('Не удалось загрузить изображение.'));
    };

    img.src = objectUrl;
  });
}
