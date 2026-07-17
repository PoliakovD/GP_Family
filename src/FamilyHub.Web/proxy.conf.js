const target = process.env['API_URL'] || 'http://localhost:5276';

module.exports = {
  '/api': {
    target,
    secure: false,
    changeOrigin: true,
    logLevel: 'info',
  },
  '/attachments': {
    target,
    secure: false,
    changeOrigin: true,
  },
  '/bot': {
    target,
    secure: false,
    changeOrigin: true,
  },
};
