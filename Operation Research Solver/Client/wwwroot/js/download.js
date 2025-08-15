
window.downloadFile = (fileName, contentType, base64Data) => {
    const a = document.createElement('a');
    a.download = fileName;
    a.href = `data:${contentType};base64,${base64Data}`;
    document.body.appendChild(a);
    a.click();
    a.remove();
};
