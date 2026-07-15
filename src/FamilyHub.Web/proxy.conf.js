const target = process.env['API_URL'] || 'http://localhost:5000';

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
};
